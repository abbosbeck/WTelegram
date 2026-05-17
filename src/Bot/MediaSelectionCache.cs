using System.Collections.Concurrent;
using Domain.Downloads;

namespace Bot;

internal sealed class MediaSelectionCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public string Store(long userId, IReadOnlyList<MediaItem> items, MediaSource source)
    {
        EvictExpired();
        var token = Guid.NewGuid().ToString("N")[..8];
        _entries[Key(userId, token)] = new Entry(items, source, DateTime.UtcNow);
        return token;
    }

    public (IReadOnlyList<MediaItem> Items, MediaSource Source)? Get(long userId, string token)
    {
        if (_entries.TryGetValue(Key(userId, token), out var e))
            return (e.Items, e.Source);
        return null;
    }

    public void Forget(long userId, string token) => _entries.TryRemove(Key(userId, token), out _);

    private void EvictExpired()
    {
        var cutoff = DateTime.UtcNow - Ttl;
        foreach (var kv in _entries)
            if (kv.Value.CreatedAt < cutoff)
                _entries.TryRemove(kv.Key, out _);
    }

    private static string Key(long userId, string token) => $"{userId}:{token}";

    private sealed record Entry(IReadOnlyList<MediaItem> Items, MediaSource Source, DateTime CreatedAt);
}

internal enum MediaSource
{
    ChatMessages,
    Stories,
}
