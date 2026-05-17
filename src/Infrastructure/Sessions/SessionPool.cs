using System.Collections.Concurrent;
using Application.Configuration;
using Application.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WTelegram;

namespace Infrastructure.Sessions;

/// <summary>
/// Owns one WTelegramClient instance per Telegram user.
/// - Clients are lazily created and re-hydrated from Postgres.
/// - Idle clients are evicted by <see cref="SessionPoolEvictionService"/>.
/// - All sessions share the same ApiId/ApiHash from <see cref="TelegramOptions"/>.
/// </summary>
public sealed class SessionPool : IAsyncDisposable
{
    private readonly TelegramOptions _telegramOptions;
    private readonly SessionOptions _sessionOptions;
    private readonly IUserSessionStore _store;
    private readonly LoginCoordinator _loginCoordinator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SessionPool> _logger;

    private readonly ConcurrentDictionary<long, Entry> _entries = new();
    private readonly SemaphoreSlim _capacityLock = new(1, 1);
    private readonly ConcurrentDictionary<long, DateTime> _floodWaitUntil = new();

    public SessionPool(
        IOptions<TelegramOptions> telegramOptions,
        IOptions<SessionOptions> sessionOptions,
        IUserSessionStore store,
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

    public async Task<Client> AcquireAsync(long userId, CancellationToken ct)
    {
        if (_telegramOptions.ApiId == 0 || string.IsNullOrWhiteSpace(_telegramOptions.ApiHash))
            throw new InvalidOperationException(
                "Telegram:ApiId / Telegram:ApiHash are not configured.");

        // Self-throttle: if Telegram told us to wait, don't even try again until then.
        if (_floodWaitUntil.TryGetValue(userId, out var until) && until > DateTime.UtcNow)
        {
            var remaining = (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds);
            throw new FloodWaitException(remaining, FormatFloodWaitMessage(remaining));
        }
        else if (until != default)
        {
            _floodWaitUntil.TryRemove(userId, out _);
        }

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

            var configCallback = BuildConfigCallback(userId);
            var client = new Client(configCallback, stream);

            try
            {
                var me = await client.LoginUserIfNeeded().WaitAsync(ct);
                stream.SetIdentity(me?.phone, BuildDisplayName(me));

                await stream.FlushNowAsync(ct);
                await _store.TouchAsync(userId, ct);
                entry.Client = client;
                _loginCoordinator.Complete(userId);
                _logger.LogInformation("Session acquired for user {UserId} ({Name})", userId, me?.username ?? me?.first_name);
                return client;
            }
            catch (TL.RpcException rpc) when (rpc.Code == 420)
            {
                // FLOOD_WAIT_X — Telegram's rate-limit. Record cooldown and rethrow cleanly.
                var seconds = rpc.X > 0 ? rpc.X : 60;
                _floodWaitUntil[userId] = DateTime.UtcNow.AddSeconds(seconds);
                client.Dispose();
                stream.Dispose();
                _entries.TryRemove(userId, out _);
                _logger.LogWarning("FLOOD_WAIT_{Seconds}s for user {UserId}", seconds, userId);
                throw new FloodWaitException(seconds, FormatFloodWaitMessage(seconds));
            }
            catch (Exception ex) when (UnwrapSessionExpired(ex) is SessionExpiredException expired)
            {
                // The stored bytes are useless — drop them so a fresh /login
                // can start cleanly instead of looping on the same dead key.
                client.Dispose();
                stream.Dispose();
                _entries.TryRemove(userId, out _);
                try { await _store.DeleteAsync(userId, ct); }
                catch (Exception delEx) { _logger.LogWarning(delEx, "Failed to delete expired session for user {UserId}", userId); }
                throw expired;
            }
            catch
            {
                client.Dispose();
                stream.Dispose();
                _entries.TryRemove(userId, out _);
                throw;
            }
        }
        finally
        {
            entry.InitLock.Release();
        }
    }

    private static string FormatFloodWaitMessage(int seconds)
    {
        var minutes = seconds / 60.0;
        return minutes >= 1
            ? $"Telegram rate-limited login: wait {seconds}s (≈ {minutes:0.#} min) before retrying."
            : $"Telegram rate-limited login: wait {seconds}s before retrying.";
    }

    public bool IsCached(long userId) =>
        _entries.TryGetValue(userId, out var e) && e.Client is not null;

    public async Task EvictAsync(long userId)
    {
        if (!_entries.TryRemove(userId, out var entry)) return;
        await DisposeEntryAsync(entry);
        _logger.LogInformation("Evicted session for user {UserId}", userId);
    }

    public IEnumerable<(long UserId, DateTime LastUsedAt)> Snapshot()
    {
        foreach (var (id, e) in _entries)
            if (e.Client is not null)
                yield return (id, e.LastUsedAt);
    }

    private Func<string, string?> BuildConfigCallback(long userId) => what => what switch
    {
        "api_id" => _telegramOptions.ApiId.ToString(),
        "api_hash" => _telegramOptions.ApiHash,
        "phone_number" => AwaitFromLogin(userId, s => s.AwaitPhoneAsync()),
        "verification_code" => AwaitFromLogin(userId, s => s.AwaitCodeAsync()),
        "password" => AwaitFromLogin(userId, s => s.AwaitPasswordAsync()),
        "session_pathname" => null,
        _ => null
    };

    private string AwaitFromLogin(long userId, Func<LoginSession, Task<string>> selector)
    {
        var login = _loginCoordinator.Get(userId)
            ?? throw new SessionExpiredException(userId);
        return selector(login).GetAwaiter().GetResult();
    }

    private async Task EnsureCapacityAsync(CancellationToken ct)
    {
        var live = _entries.Values.Count(e => e.Client is not null);
        if (live < _sessionOptions.MaxConcurrentSessions) return;

        await _capacityLock.WaitAsync(ct);
        try
        {
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

    private static SessionExpiredException? UnwrapSessionExpired(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is SessionExpiredException se) return se;
        return null;
    }

    private static string? BuildDisplayName(TL.User? me)
    {
        if (me is null) return null;
        var name = $"{me.first_name} {me.last_name}".Trim();
        if (!string.IsNullOrWhiteSpace(me.username))
            name = string.IsNullOrEmpty(name) ? "@" + me.username : $"{name} (@{me.username})";
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static Task DisposeEntryAsync(Entry entry)
    {
        var client = entry.Client;
        entry.Client = null;
        if (client is not null)
        {
            try { client.Dispose(); }
            catch { /* ignore */ }
        }
        return Task.CompletedTask;
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
