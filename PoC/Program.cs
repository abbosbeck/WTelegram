using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TelegramDownloader.Configuration;
using TelegramDownloader.Data;
using TelegramDownloader.Security;
using TelegramDownloader.Services;
using TelegramDownloader.Ui;

namespace TelegramDownloader;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // One-shot helpers
        if (args.Length > 0)
        {
            var cmd = args[0].Trim().ToLowerInvariant();
            switch (cmd)
            {
                case "gen-key":
                case "genkey":
                case "get-key":   // common typo
                case "generate-key":
                    Console.WriteLine(AesGcmSessionCipher.GenerateKeyBase64());
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Store this in user-secrets:");
                    Console.Error.WriteLine("  dotnet user-secrets set \"Sessions:EncryptionKey\" \"<paste>\"");
                    Console.Error.WriteLine("…or set the SESSIONS__ENCRYPTIONKEY environment variable.");
                    return 0;
            }
        }

        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddUserSecrets<TelegramOptions>(optional: true)
            .AddEnvironmentVariables();

        // Options
        builder.Services
            .AddOptions<TelegramOptions>()
            .Bind(builder.Configuration.GetSection(TelegramOptions.SectionName))
            .ValidateOnStart();

        builder.Services
            .AddOptions<WebDownloaderOptions>()
            .Bind(builder.Configuration.GetSection(WebDownloaderOptions.SectionName));

        builder.Services
            .AddOptions<SessionOptions>()
            .Bind(builder.Configuration.GetSection(SessionOptions.SectionName))
            .ValidateOnStart();

        // EF Core + Postgres
        var connectionString = builder.Configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Postgres is not configured. Set it via user-secrets or env var ConnectionStrings__Postgres.");
        builder.Services.AddDbContextFactory<AppDbContext>(opts => opts.UseNpgsql(connectionString));

        // Security / sessions
        builder.Services.AddSingleton<ISessionCipher, AesGcmSessionCipher>();
        builder.Services.AddSingleton<UserSessionStore>();
        builder.Services.AddSingleton<LoginCoordinator>();
        builder.Services.AddSingleton<SessionPool>();
        builder.Services.AddHostedService<SessionPoolEvictionService>();

        // Console UI + domain services
        builder.Services.AddSingleton<IConsolePrompt, ConsolePrompt>();
        builder.Services.AddSingleton<DownloadManifest>();
        builder.Services.AddSingleton<MessageLinkResolver>();
        builder.Services.AddSingleton<TelegramService>();
        builder.Services.AddSingleton<WebVideoDownloader>();
        builder.Services.AddHostedService<ConsoleUi>();

        using var host = builder.Build();

        // Validate AES key eagerly + ensure DB schema exists.
        _ = host.Services.GetRequiredService<ISessionCipher>();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.Database.MigrateAsync();
        }

        var wtLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WTelegram");
        WTelegram.Helpers.Log = (level, message) => wtLogger.Log((LogLevel)level, "{Message}", message);

        await host.RunAsync();
        return 0;
    }
}
