using Application.Security;
using Application.Sessions;
using Domain.Sessions;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Sessions;

public sealed class UserSessionStore : IUserSessionStore
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ISessionCipher _cipher;
    private readonly ILogger<UserSessionStore> _logger;

    public UserSessionStore(
        IDbContextFactory<AppDbContext> dbFactory,
        ISessionCipher cipher,
        ILogger<UserSessionStore> logger)
    {
        _dbFactory = dbFactory;
        _cipher = cipher;
        _logger = logger;
    }

    public async Task<byte[]?> LoadAsync(long userId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.UserSessions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TelegramUserId == userId && x.IsActive, ct);
        if (row is null) return null;
        try
        {
            return _cipher.Decrypt(row.SessionBytes, row.Nonce, row.Tag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to decrypt session for user {UserId}. The encryption key may have changed.", userId);
            return null;
        }
    }

    public async Task SaveAsync(long userId, byte[] sessionBytes, string? phone, string? displayName, CancellationToken ct = default)
    {
        var (cipher, nonce, tag) = _cipher.Encrypt(sessionBytes);
        var now = DateTime.UtcNow;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.UserSessions.FirstOrDefaultAsync(x => x.TelegramUserId == userId, ct);
        if (existing is null)
        {
            db.UserSessions.Add(new UserSession
            {
                TelegramUserId = userId,
                PhoneNumber = phone,
                DisplayName = displayName,
                SessionBytes = cipher,
                Nonce = nonce,
                Tag = tag,
                CreatedAt = now,
                LastUsedAt = now,
                IsActive = true
            });
        }
        else
        {
            existing.SessionBytes = cipher;
            existing.Nonce = nonce;
            existing.Tag = tag;
            existing.LastUsedAt = now;
            existing.IsActive = true;
            if (!string.IsNullOrWhiteSpace(phone)) existing.PhoneNumber = phone;
            if (!string.IsNullOrWhiteSpace(displayName)) existing.DisplayName = displayName;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task TouchAsync(long userId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.UserSessions.FirstOrDefaultAsync(x => x.TelegramUserId == userId, ct);
        if (row is null) return;
        row.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(long userId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.UserSessions.Where(x => x.TelegramUserId == userId).ExecuteDeleteAsync(ct);
    }
}
