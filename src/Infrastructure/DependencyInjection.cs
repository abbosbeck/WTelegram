using Application.Configuration;
using Application.Security;
using Application.Sessions;
using Infrastructure.Data;
using Infrastructure.Downloads;
using Infrastructure.Security;
using Infrastructure.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Options
        services.AddOptions<TelegramOptions>()
            .Bind(configuration.GetSection(TelegramOptions.SectionName));
        services.AddOptions<WebDownloaderOptions>()
            .Bind(configuration.GetSection(WebDownloaderOptions.SectionName));
        services.AddOptions<SessionOptions>()
            .Bind(configuration.GetSection(SessionOptions.SectionName));

        // EF Core + Postgres
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Postgres is not configured. " +
                "Set it via user-secrets or env var ConnectionStrings__Postgres.");
        services.AddDbContextFactory<AppDbContext>(opts => opts.UseNpgsql(connectionString));

        // Security / sessions
        services.AddSingleton<ISessionCipher, AesGcmSessionCipher>();
        services.AddSingleton<IUserSessionStore, UserSessionStore>();
        services.AddSingleton<SessionPool>();
        services.AddHostedService<SessionPoolEvictionService>();

        // Downloads
        services.AddSingleton<DownloadManifest>();
        services.AddSingleton<MessageLinkResolver>();
        services.AddSingleton<TelegramService>();
        services.AddSingleton<WebVideoDownloader>();

        return services;
    }
}
