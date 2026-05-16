namespace Application.Configuration;

public sealed class WebDownloaderOptions
{
    public const string SectionName = "WebDownloader";

    public string ToolsDirectory { get; set; } = "tools";
    public string Format { get; set; } = "bestvideo*+bestaudio/best";
    public string OutputTemplate { get; set; } = "%(title)s [%(id)s].%(ext)s";
    public string? CookiesPath { get; set; }
    public bool AutoUpdate { get; set; } = true;

    public string ResolvedToolsDirectory =>
        Path.IsPathRooted(ToolsDirectory)
            ? ToolsDirectory
            : Path.Combine(AppContext.BaseDirectory, ToolsDirectory);
}
