using Application.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace Infrastructure.Downloads;

/// <summary>
/// Downloads videos/audio from any site supported by yt-dlp.
/// </summary>
public sealed class WebVideoDownloader
{
    private readonly TelegramOptions _telegramOptions;
    private readonly WebDownloaderOptions _options;
    private readonly DownloadManifest _manifest;
    private readonly ILogger<WebVideoDownloader> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private YoutubeDL? _ytdl;

    public WebVideoDownloader(
        IOptions<TelegramOptions> telegramOptions,
        IOptions<WebDownloaderOptions> options,
        DownloadManifest manifest,
        ILogger<WebVideoDownloader> logger)
    {
        _telegramOptions = telegramOptions.Value;
        _options = options.Value;
        _manifest = manifest;
        _logger = logger;
    }

    private async Task<YoutubeDL> EnsureReadyAsync(CancellationToken ct)
    {
        if (_ytdl is not null) return _ytdl;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_ytdl is not null) return _ytdl;

            var toolsDir = _options.ResolvedToolsDirectory;
            Directory.CreateDirectory(toolsDir);

            var ytDlpName = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";
            var ffmpegName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            var ytDlpPath = Path.Combine(toolsDir, ytDlpName);
            var ffmpegPath = Path.Combine(toolsDir, ffmpegName);

            if (!File.Exists(ytDlpPath))
            {
                _logger.LogInformation("Downloading yt-dlp into {Dir}…", toolsDir);
                await Utils.DownloadYtDlp(toolsDir);
            }
            else if (_options.AutoUpdate)
            {
                _logger.LogInformation("Updating yt-dlp…");
                try { await Utils.DownloadYtDlp(toolsDir); }
                catch (Exception ex) { _logger.LogWarning(ex, "yt-dlp self-update failed; continuing with existing binary"); }
            }

            if (!File.Exists(ffmpegPath))
            {
                _logger.LogInformation("Downloading ffmpeg into {Dir}…", toolsDir);
                await Utils.DownloadFFmpeg(toolsDir);
            }

            _ytdl = new YoutubeDL
            {
                YoutubeDLPath = ytDlpPath,
                FFmpegPath = ffmpegPath,
                OutputFolder = _telegramOptions.ResolvedOutputDirectory,
                OutputFileTemplate = _options.OutputTemplate,
                RestrictFilenames = false,
                OverwriteFiles = false
            };

            _logger.LogInformation("yt-dlp ready ({Path})", ytDlpPath);
            return _ytdl;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<string?> DownloadAsync(
        string url,
        bool audioOnly,
        Action<DownloadProgress>? onProgress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is required", nameof(url));

        var key = audioOnly ? $"audio:{url}" : $"video:{url}";
        if (_manifest.ContainsUrl(key))
        {
            _logger.LogInformation("Skipping already-downloaded URL {Url}", url);
            return _manifest.GetUrlPath(key);
        }

        var ytdl = await EnsureReadyAsync(ct);

        var overrideOptions = new OptionSet
        {
            Format = audioOnly ? "bestaudio" : _options.Format,
            NoPlaylist = true
        };
        if (!string.IsNullOrWhiteSpace(_options.CookiesPath) && File.Exists(_options.CookiesPath))
            overrideOptions.Cookies = _options.CookiesPath;

        var progress = onProgress is null ? null : new Progress<DownloadProgress>(onProgress);
        var output = new Progress<string>(line =>
        {
            if (!string.IsNullOrWhiteSpace(line))
                _logger.LogInformation("yt-dlp: {Line}", line);
        });

        _logger.LogInformation("Starting yt-dlp download: {Url}", url);

        var result = audioOnly
            ? await ytdl.RunAudioDownload(url, AudioConversionFormat.Mp3, ct, progress, output, overrideOptions)
            : await ytdl.RunVideoDownload(url, progress: progress, ct: ct, output: output, overrideOptions: overrideOptions);

        if (!result.Success)
        {
            var error = string.Join(" | ", result.ErrorOutput ?? Array.Empty<string>());
            throw new InvalidOperationException($"yt-dlp failed: {error}");
        }

        var path = result.Data;
        _manifest.RecordUrl(key, path);
        _logger.LogInformation("Saved {Path}", path);
        return path;
    }
}
