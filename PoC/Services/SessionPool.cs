using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelegramDownloader.Configuration;
using WTelegram;

namespace TelegramDownloader.Services;

/// <summary>
/// Owns one WTelegramClient instance per Telegram user.
/// - Clients are lazily created and re-hydrated from Postgres.
/// - Idle clients are evicted by <see cref="SessionPoolEvictionService"/>.
/// - All sessions share the same ApiId/ApiHash from <see cref="TelegramOptions"/>.
/// </summary>
internal sealed class SessionPool : IAsyncDisposable
{
    private readonly TelegramOptions _telegramOptions;
    private readonly SessionOptions _sessionOptions;
    private readonly UserSessionStore _store;
    private readonly LoginCoordinator _loginCoordinator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SessionPool> _logger;

    private readonly ConcurrentDictionary<long, Entry> _entries = new();
    private readonly SemaphoreSlim _capacityLock = new(1, 1);

    public SessionPool(
        IOptions<TelegramOptions> telegramOptions,
        IOptions<SessionOptions> sessionOptions,
        UserSessionStore store,
        LoginCoordinator loginCoordinator,
        ILoggerFactory loggerFactory)
    {
        _telegramOptions = telegramOptions.Value;
        _sessionOptions = sessionOptions.Value;
        _store = store;
        _loginCoordinator = loginCoordinator;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SessionPool>();
    }

    /// <summary>
    /// Returns a connected, logged-in <see cref="Client"/> for the given user.
    /// If no session exists yet, the caller must register a <see cref="LoginSession"/>
    /// via <see cref="LoginCoordinator"/> before invoking this; otherwise the
    /// config callback will throw when WTelegramClient asks for the phone number.
    /// </summary>
    public async Task<Client> AcquireAsync(long userId, CancellationToken ct)
    {
        if (_telegramOptions.ApiId == 0 || string.IsNullOrWhiteSpace(_telegramOptions.ApiHash))
            throw new InvalidOperationException(
                "Telegram:ApiId / Telegram:ApiHash are not configured.");

        var entry = _entries.GetOrAdd(userId, id => new Entry(id));
        await entry.InitLock.WaitAsync(ct);
        try
        {
            entry.LastUsedAt = DateTime.UtcNow;
            if (entry.Client is not null) return entry.Client;

            await EnsureCapacityAsync(ct);

            var initial = await _store.LoadAsync(userId, ct);
            var stream = new PostgresSessionStream(userId, initial, _store,
                _loggerFactory.CreateLogger<PostgresSessionStream>());

            var configCallback = BuildConfigCallback(userId, stream);
            var client = new Client(configCallback, stream);

            try
            {
                var me = await client.LoginUserIfNeeded().WaitAsync(ct);
                stream.SetIdentity(me?.phone, BuildDisplayName(me));
                await _store.TouchAsync(userId, ct);
                entry.Client = client;
                _loginCoordinator.Complete(userId);
                _logger.LogInformation("Session acquired for user {UserId} ({Name})", userId, me?.username ?? me?.first_name);
                return client;
            }
            catch
            {
                client.Dispose();
                stream.Dispose();
                throw;
            }
        }
        finally
        {
            entry.InitLock.Release();
        }
    }

    /// <summary>Whether a connected client is currently cached for this user.</summary>
    public bool IsCached(long userId) =>
        _entries.TryGetValue(userId, out var e) && e.Client is not null;

    public async Task EvictAsync(long userId)
    {
        if (!_entries.TryRemove(userId, out var entry)) return;
        await DisposeEntryAsync(entry);
        _logger.LogInformation("Evicted session for user {UserId}", userId);
    }

    internal IEnumerable<(long UserId, DateTime LastUsedAt)> Snapshot()
    {
        foreach (var (id, e) in _entries)
            if (e.Client is not null)
                yield return (id, e.LastUsedAt);
    }

    private Func<string, string?> BuildConfigCallback(long userId, PostgresSessionStream stream) => what => what switch
    {
        "api_id" => _telegramOptions.ApiId.ToString(),
        "api_hash" => _telegramOptions.ApiHash,
        "phone_number" => AwaitFromLogin(userId, s => s.AwaitPhoneAsync()),
        "verification_code" => AwaitFromLogin(userId, s => s.AwaitCodeAsync()),
        "password" => AwaitFromLogin(userId, s => s.AwaitPasswordAsync()),
        // Required by WTelegramClient, but ignored when a Stream is supplied.
        "session_pathname" => null,
        _ => null
    };

    private string AwaitFromLogin(long userId, Func<LoginSession, Task<string>> selector)
    {
        var login = _loginCoordinator.Get(userId)
            ?? throw new InvalidOperationException(
                $"WTelegramClient asked for credentials for user {userId} but no LoginSession is registered.");
        return selector(login).GetAwaiter().GetResult();
    }

    private async Task EnsureCapacityAsync(CancellationToken ct)
    {
        var live = _entries.Values.Count(e => e.Client is not null);
        if (live < _sessionOptions.MaxConcurrentSessions) return;

        await _capacityLock.WaitAsync(ct);
        try
        {
            // Drop the oldest idle client to make room.
            var oldest = _entries
                .Where(kv => kv.Value.Client is not null)
                .OrderBy(kv => kv.Value.LastUsedAt)
                .FirstOrDefault();
            if (oldest.Value is not null)
                await EvictAsync(oldest.Key);
        }
        finally
        {
            _capacityLock.Release();
        }
    }

    private static string? BuildDisplayName(TL.User? me)
    {
        if (me is null) return null;
        var name = $"{me.first_name} {me.last_name}".Trim();
        if (!string.IsNullOrWhiteSpace(me.username))
            name = string.IsNullOrEmpty(name) ? "@" + me.username : $"{name} (@{me.username})";
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static async Task DisposeEntryAsync(Entry entry)
    {
        var client = entry.Client;
        entry.Client = null;
        if (client is not null)
        {
            try { client.Dispose(); }
            catch { /* ignore */ }
        }
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (id, _) in _entries.ToArray())
            await EvictAsync(id);
    }

    private sealed class Entry
    {
        public Entry(long userId) { UserId = userId; }
        public long UserId { get; }
        public Client? Client { get; set; }
        public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
        public SemaphoreSlim InitLock { get; } = new(1, 1);
    }
}
