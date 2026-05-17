using Application.Sessions;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Sessions;

/// <summary>
/// In-memory MemoryStream backing WTelegramClient's session.
/// Auto-flushes the latest bytes to <see cref="IUserSessionStore"/> on close
/// and on each write (debounced).
/// </summary>
internal sealed class PostgresSessionStream : Stream
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromSeconds(1);

    private readonly long _userId;
    private readonly IUserSessionStore _store;
    private readonly ILogger _logger;
    private readonly MemoryStream _buffer;
    private readonly Lock _flushLock = new();

    private string? _phone;
    private string? _displayName;
    private DateTime _lastFlush = DateTime.MinValue;
    private bool _dirty;
    private bool _disposed;

    public PostgresSessionStream(long userId, byte[]? initial, IUserSessionStore store, ILogger logger)
    {
        _userId = userId;
        _store = store;
        _logger = logger;
        _buffer = initial is { Length: > 0 } ? new MemoryStream(initial.Length) : new MemoryStream();
        if (initial is { Length: > 0 })
        {
            _buffer.Write(initial, 0, initial.Length);
            _buffer.Position = 0;
        }
    }

    public void SetIdentity(string? phone, string? displayName)
    {
        _phone = phone;
        _displayName = displayName;

        byte[] snapshot;
        lock (_flushLock)
        {
            snapshot = _buffer.ToArray();
            _dirty = false;
            _lastFlush = DateTime.UtcNow;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _store.SaveAsync(_userId, snapshot, phone, displayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist session identity for user {UserId}", _userId);
            }
        });
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _buffer.Length;
    public override long Position { get => _buffer.Position; set => _buffer.Position = value; }

    public override int Read(byte[] buffer, int offset, int count) => _buffer.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _buffer.Seek(offset, origin);
    public override void SetLength(long value) { _buffer.SetLength(value); MarkDirty(); }
    public override void Flush() => MaybeFlushNow(force: false);

    public override void Write(byte[] buffer, int offset, int count)
    {
        _buffer.Write(buffer, offset, count);
        MarkDirty();
        MaybeFlushNow(force: false);
    }

    private void MarkDirty()
    {
        lock (_flushLock) { _dirty = true; }
    }

    private void MaybeFlushNow(bool force)
    {
        byte[]? snapshot = null;
        lock (_flushLock)
        {
            if (!_dirty) return;
            var now = DateTime.UtcNow;
            if (!force && now - _lastFlush < DebounceInterval) return;
            snapshot = _buffer.ToArray();
            _dirty = false;
            _lastFlush = now;
        }

        var phone = _phone;
        var name = _displayName;
        _ = Task.Run(async () =>
        {
            try
            {
                await _store.SaveAsync(_userId, snapshot!, phone, name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist session for user {UserId}", _userId);
            }
        });
    }

    /// <summary>
    /// Synchronously flushes any pending bytes. Used on Dispose so the process
    /// can't exit before the latest auth_key / salt rotation has landed in Postgres.
    /// </summary>
    private void FlushSync()
    {
        byte[]? snapshot;
        string? phone;
        string? name;
        lock (_flushLock)
        {
            if (!_dirty) return;
            snapshot = _buffer.ToArray();
            phone = _phone;
            name = _displayName;
            _dirty = false;
            _lastFlush = DateTime.UtcNow;
        }

        try
        {
            // Block intentionally: Stream.Dispose is sync, and losing the final
            // delta on shutdown is exactly the bug we're closing.
            _store.SaveAsync(_userId, snapshot!, phone, name).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist final session bytes for user {UserId} on dispose", _userId);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) { base.Dispose(disposing); return; }
        _disposed = true;
        if (disposing)
        {
            try { FlushSync(); } catch { /* swallow on dispose */ }
            _buffer.Dispose();
        }
        base.Dispose(disposing);
    }
}
