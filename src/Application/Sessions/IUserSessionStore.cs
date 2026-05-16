namespace Application.Sessions;

/// <summary>
/// Persists encrypted WTelegramClient session blobs.
/// Implemented in Infrastructure (Postgres).
/// </summary>
public interface IUserSessionStore
{
    Task<byte[]?> LoadAsync(long userId, CancellationToken ct = default);
    Task SaveAsync(long userId, byte[] sessionBytes, string? phone, string? displayName, CancellationToken ct = default);
    Task TouchAsync(long userId, CancellationToken ct = default);
    Task DeleteAsync(long userId, CancellationToken ct = default);
}
