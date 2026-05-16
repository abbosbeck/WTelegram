namespace Application.Configuration;

public sealed class SessionOptions
{
    public const string SectionName = "Sessions";

    /// <summary>Base64-encoded 32-byte AES-GCM key. Required.</summary>
    public string EncryptionKey { get; set; } = "";

    public int MaxConcurrentSessions { get; set; } = 200;
    public int IdleEvictionMinutes { get; set; } = 15;
}
