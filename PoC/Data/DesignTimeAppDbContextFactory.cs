using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace TelegramDownloader.Data;

internal sealed class DesignTimeAppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddUserSecrets<DesignTimeAppDbContextFactory>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var cs = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Postgres is not configured.");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(cs)
            .Options;
        return new AppDbContext(options);
    }
}
