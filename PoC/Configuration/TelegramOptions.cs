namespace TelegramDownloader.Configuration;

internal sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public int ApiId { get; set; }
    public string ApiHash { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string SessionPathname { get; set; } = "telegram_session.dat";

    public string ResolvedOutputDirectory =>
        string.IsNullOrWhiteSpace(OutputDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "TelegramDownloads")
            : OutputDirectory;

    public string ResolvedSessionPathname =>
        Path.IsPathRooted(SessionPathname)
            ? SessionPathname
            : Path.Combine(ResolvedOutputDirectory, SessionPathname);
}
