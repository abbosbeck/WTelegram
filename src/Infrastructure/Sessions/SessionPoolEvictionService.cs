using Application.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Sessions;

public sealed class SessionPoolEvictionService : BackgroundService
{
    private readonly SessionPool _pool;
    private readonly SessionOptions _options;
    private readonly ILogger<SessionPoolEvictionService> _logger;

    public SessionPoolEvictionService(
        SessionPool pool,
        IOptions<SessionOptions> options,
        ILogger<SessionPoolEvictionService> logger)
    {
        _pool = pool;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.IdleEvictionMinutes / 2.0));
        var threshold = TimeSpan.FromMinutes(Math.Max(1, _options.IdleEvictionMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                var cutoff = DateTime.UtcNow - threshold;
                foreach (var (userId, lastUsed) in _pool.Snapshot().ToArray())
                {
                    if (lastUsed < cutoff)
                        await _pool.EvictAsync(userId);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Eviction sweep failed");
            }
        }
    }
}
