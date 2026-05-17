using Application.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
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
                _logger.LogInformation("Downloading ffmpeg into {Dir} (this is a one-time ~80MB download)…", toolsDir);
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

    /// <summary>
    /// Pre-warms yt-dlp and ffmpeg in the background so user-facing /url
    /// requests don't pay the (potentially multi-minute) one-time download cost.
    /// Safe to call multiple times; safe to fire-and-forget.
    /// </summary>
    public async Task WarmupAsync(CancellationToken ct)
    {
        try
        {
            await EnsureReadyAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "yt-dlp/ffmpeg warmup failed; will retry on first user request");
        }
    }

    public async Task<string?> DownloadAsync(
        string url,
        bool audioOnly,
        Action<DownloadProgress>? onProgress,
        CancellationToken ct)
        => await DownloadAsync(url, audioOnly, maxHeight: null, onProgress, ct);

    /// <summary>
    /// Downloads from <paramref name="url"/>. If <paramref name="maxHeight"/> is set,
    /// caps the video height (e.g. 720 for 720p). Audio-only downloads ignore it.
    /// Manifest keys include the chosen quality so different qualities don't collide.
    /// </summary>
    public async Task<string?> DownloadAsync(
        string url,
        bool audioOnly,
        int? maxHeight,
        Action<DownloadProgress>? onProgress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is required", nameof(url));

        var key = BuildManifestKey(url, audioOnly, maxHeight);
        if (_manifest.ContainsUrl(key))
        {
            _logger.LogInformation("Skipping already-downloaded URL {Url}", url);
            return _manifest.GetUrlPath(key);
        }

        var ytdl = await EnsureReadyAsync(ct);

        var format = audioOnly
            ? "bestaudio"
            : maxHeight is int h
                ? $"bestvideo[height<={h}]+bestaudio/best[height<={h}]"
                : _options.Format;

        var overrideOptions = new OptionSet
        {
            Format = format,
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

        _logger.LogInformation("Starting yt-dlp download: {Url} (format={Format})", url, format);

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

    /// <summary>
    /// Inspects the URL via <c>yt-dlp --dump-json</c> (no download) and returns the
    /// distinct video heights and whether an audio-only track is available.
    /// </summary>
    public async Task<UrlFormatInfo> FetchFormatsAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is required", nameof(url));

        var ytdl = await EnsureReadyAsync(ct);
        var fetch = await ytdl.RunVideoDataFetch(url, ct);
        if (!fetch.Success || fetch.Data is null)
        {
            var error = string.Join(" | ", fetch.ErrorOutput ?? Array.Empty<string>());
            throw new InvalidOperationException($"yt-dlp metadata fetch failed: {error}");
        }

        var data = fetch.Data;
        var heights = new SortedSet<int>();
        long? approxAudioSize = null;
        var sizeByHeight = new Dictionary<int, long>();

        foreach (var f in data.Formats ?? [])
        {
            // Skip formats with no video stream (audio-only entries) for height aggregation.
            var hasVideo = !string.IsNullOrEmpty(f.VideoCodec) && f.VideoCodec != "none";
            var hasAudio = !string.IsNullOrEmpty(f.AudioCodec) && f.AudioCodec != "none";

            if (hasVideo && f.Height is int hh && hh > 0)
            {
                heights.Add(hh);
                var fsize = (long?)(f.FileSize ?? f.ApproximateFileSize) ?? 0L;
                if (fsize > 0 && (!sizeByHeight.TryGetValue(hh, out var prev) || fsize > prev))
                    sizeByHeight[hh] = fsize;
            }
            else if (hasAudio && !hasVideo)
            {
                var fsize = (long?)(f.FileSize ?? f.ApproximateFileSize) ?? 0L;
                if (fsize > 0 && (approxAudioSize is null || fsize > approxAudioSize))
                    approxAudioSize = fsize;
            }
        }

        return new UrlFormatInfo(
            Title: data.Title,
            Heights: heights.ToArray(),
            SizeByHeight: sizeByHeight,
            ApproxAudioSize: approxAudioSize);
    }

    private static string BuildManifestKey(string url, bool audioOnly, int? maxHeight) =>
        audioOnly ? $"audio:{url}"
        : maxHeight is int h ? $"video:{h}:{url}"
        : $"video:{url}";
}

/// <summary>Format discovery result for a single URL.</summary>
public sealed record UrlFormatInfo(
    string? Title,
    int[] Heights,
    IReadOnlyDictionary<int, long> SizeByHeight,
    long? ApproxAudioSize);
