namespace TelegramDownloader.Data;

internal sealed class UserSession
{
    /// <summary>Telegram numeric user ID. Primary key.</summary>
    public long TelegramUserId { get; set; }

    public string? PhoneNumber { get; set; }
    public string? DisplayName { get; set; }

    /// <summary>AES-GCM ciphertext of the WTelegramClient session blob.</summary>
    public byte[] SessionBytes { get; set; } = Array.Empty<byte>();

    /// <summary>12-byte nonce used for the AES-GCM encryption of <see cref="SessionBytes"/>.</summary>
    public byte[] Nonce { get; set; } = Array.Empty<byte>();

    /// <summary>16-byte authentication tag from AES-GCM.</summary>
    public byte[] Tag { get; set; } = Array.Empty<byte>();

    public DateTime CreatedAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public bool IsActive { get; set; } = true;
}
