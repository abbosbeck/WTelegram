using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TelegramDownloader.Configuration;
using TelegramDownloader.Services;
using TelegramDownloader.Ui;
using WTelegram;

namespace TelegramDownloader;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddUserSecrets<TelegramOptions>(optional: true)
            .AddEnvironmentVariables();

        builder.Services
            .AddOptions<TelegramOptions>()
            .Bind(builder.Configuration.GetSection(TelegramOptions.SectionName))
            .ValidateOnStart();

        builder.Services.AddSingleton<IConsolePrompt, ConsolePrompt>();
        builder.Services.AddSingleton<WTelegramConfigProvider>();
        builder.Services.AddSingleton<Client>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<TelegramOptions>>().Value;
            Directory.CreateDirectory(options.ResolvedOutputDirectory);
            var provider = sp.GetRequiredService<WTelegramConfigProvider>();
            return new Client(provider.Provide);
        });
        builder.Services.AddSingleton<DownloadManifest>();
        builder.Services.AddSingleton<MessageLinkResolver>();
        builder.Services.AddSingleton<TelegramService>();
        builder.Services.AddHostedService<ConsoleUi>();

        using var host = builder.Build();
        await host.RunAsync();
    }
}
