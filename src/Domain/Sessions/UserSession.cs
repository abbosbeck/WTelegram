namespace Domain.Sessions;

public sealed class UserSession
{
    public long TelegramUserId { get; set; }
    public string? PhoneNumber { get; set; }
    public string? DisplayName { get; set; }
    public byte[] SessionBytes { get; set; } = Array.Empty<byte>();
    public byte[] Nonce { get; set; } = Array.Empty<byte>();
    public byte[] Tag { get; set; } = Array.Empty<byte>();
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public bool IsActive { get; set; } = true;
}
