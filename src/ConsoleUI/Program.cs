using Application;
using Application.Configuration;
using Application.Security;
using Infrastructure;
using Infrastructure.Data;
using Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SysConsole = System.Console;

namespace ConsoleUI;

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
                case "get-key":
                case "generate-key":
                    SysConsole.WriteLine(AesGcmSessionCipher.GenerateKeyBase64());
                    SysConsole.Error.WriteLine();
                    SysConsole.Error.WriteLine("Store this in user-secrets:");
                    SysConsole.Error.WriteLine("  dotnet user-secrets set \"Sessions:EncryptionKey\" \"<paste>\"");
                    SysConsole.Error.WriteLine("…or set the SESSIONS__ENCRYPTIONKEY environment variable.");
                    return 0;
            }
        }

        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddUserSecrets<TelegramOptions>(optional: true)
            .AddEnvironmentVariables();

        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);

        // Console-specific
        builder.Services.AddSingleton<IConsolePrompt, ConsolePrompt>();
        builder.Services.AddHostedService<ConsoleUi>();

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

        await host.RunAsync();
        return 0;
    }
}
