using System.Collections.Concurrent;
using Infrastructure.Downloads;

namespace Bot;

/// <summary>
/// Per-user, short-lived cache of context needed by inline-keyboard callbacks
/// whose payload is too large for Telegram's 64-byte callback_data limit
/// (e.g. a full URL plus its discovered formats).
/// </summary>
internal sealed class PendingActionCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, UrlEntry> _urls = new();

    public string StoreUrl(long userId, string url, UrlFormatInfo info)
    {
        EvictExpired();
        var token = Guid.NewGuid().ToString("N")[..8];
        _urls[Key(userId, token)] = new UrlEntry(url, info, DateTime.UtcNow);
        return token;
    }

    public (string Url, UrlFormatInfo Info)? GetUrl(long userId, string token)
    {
        if (_urls.TryGetValue(Key(userId, token), out var e))
            return (e.Url, e.Info);
        return null;
    }

    public void ForgetUrl(long userId, string token) => _urls.TryRemove(Key(userId, token), out _);

    private void EvictExpired()
    {
        var cutoff = DateTime.UtcNow - Ttl;
        foreach (var kv in _urls)
            if (kv.Value.CreatedAt < cutoff)
                _urls.TryRemove(kv.Key, out _);
    }

    private static string Key(long userId, string token) => $"{userId}:{token}";

    private sealed record UrlEntry(string Url, UrlFormatInfo Info, DateTime CreatedAt);
}
