using Application;
using Application.Configuration;
using Application.Security;
using Infrastructure;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace Bot;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddUserSecrets<BotOptions>(optional: true)
            .AddEnvironmentVariables();

        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);

        builder.Services.AddOptions<BotOptions>()
            .Bind(builder.Configuration.GetSection(BotOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Token),
                "Bot:Token is not configured. Set it via user-secrets or env var BOT__TOKEN.")
            .ValidateOnStart();

        builder.Services.AddSingleton<ITelegramBotClient>(sp =>
        {
            var token = sp.GetRequiredService<IOptions<BotOptions>>().Value.Token;
            return new TelegramBotClient(token);
        });

        builder.Services.AddSingleton<MediaSelectionCache>();
        builder.Services.AddSingleton<PendingActionCache>();
        builder.Services.AddSingleton<BotMediaSender>();
        builder.Services.AddHostedService<BotUpdateHandler>();

        using var host = builder.Build();

        // Validate AES key eagerly + apply EF migrations.
        _ = host.Services.GetRequiredService<ISessionCipher>();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.Database.MigrateAsync();
        }

        var wtLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WTelegram");
        WTelegram.Helpers.Log = (level, message) => wtLogger.Log((LogLevel)level, "{Message}", message);

        // Pre-warm yt-dlp + ffmpeg in the background so the first /url request
        // doesn't pay the cold-start download cost (ffmpeg is ~80 MB).
        var webDownloader = host.Services.GetRequiredService<Infrastructure.Downloads.WebVideoDownloader>();
        _ = Task.Run(() => webDownloader.WarmupAsync(CancellationToken.None));

        await host.RunAsync();
        return 0;
    }
}
