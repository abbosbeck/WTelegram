using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelegramDownloader.Configuration;

namespace TelegramDownloader.Services;

/// <summary>
/// Tracks already-downloaded (chatId, msgId) pairs in a JSON file under the output directory
/// so we can skip duplicates across runs.
/// </summary>
internal sealed class DownloadManifest
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<DownloadManifest> _logger;
    private readonly ConcurrentDictionary<string, string> _entries; // key -> file path
    private readonly object _saveLock = new();

    public DownloadManifest(IOptions<TelegramOptions> options, ILogger<DownloadManifest> logger)
    {
        _logger = logger;
        var opts = options.Value;
        Directory.CreateDirectory(opts.ResolvedOutputDirectory);
        _path = Path.Combine(opts.ResolvedOutputDirectory, opts.ManifestFileName);
        _entries = Load(_path, logger);
    }

    public bool Contains(long chatId, int msgId) => _entries.ContainsKey(Key(chatId, msgId));

    public void Record(long chatId, int msgId, string filePath)
    {
        _entries[Key(chatId, msgId)] = filePath;
        Save();
    }

    private static string Key(long chatId, int msgId) => $"{chatId}:{msgId}";

    private void Save()
    {
        lock (_saveLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_entries, JsonOpts);
                File.WriteAllText(_path, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write manifest {Path}", _path);
            }
        }
    }

    private static ConcurrentDictionary<string, string> Load(string path, ILogger logger)
    {
        if (!File.Exists(path)) return new ConcurrentDictionary<string, string>();
        try
        {
            var json = File.ReadAllText(path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new Dictionary<string, string>();
            return new ConcurrentDictionary<string, string>(dict);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read manifest {Path}; starting fresh", path);
            return new ConcurrentDictionary<string, string>();
        }
    }
}
