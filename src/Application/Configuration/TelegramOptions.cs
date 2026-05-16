namespace Application.Configuration;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public int ApiId { get; set; }
    public string ApiHash { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string SessionPathname { get; set; } = "telegram_session.dat";
    public string ManifestFileName { get; set; } = ".downloaded.json";
    public int MaxConcurrentDownloads { get; set; } = 3;

    /// <summary>
    /// Telegram numeric user ID of the operator. The console UI uses this
    /// to identify which session in Postgres to drive.
    /// </summary>
    public long OwnerUserId { get; set; }

    public string ResolvedOutputDirectory =>
        string.IsNullOrWhiteSpace(OutputDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "TelegramDownloads")
            : OutputDirectory;

    public string ResolvedSessionPathname =>
        Path.IsPathRooted(SessionPathname)
            ? SessionPathname
            : Path.Combine(ResolvedOutputDirectory, SessionPathname);
}
