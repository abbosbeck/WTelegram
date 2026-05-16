using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Data;

internal sealed class DesignTimeAppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var consoleUiPath = Path.GetFullPath(
            Path.Combine(Directory.GetCurrentDirectory(), "..", "ConsoleUI"));

        var config = new ConfigurationBuilder()
            .SetBasePath(consoleUiPath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var cs = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException(
                $"Connection string 'Postgres' was not found. Looked in '{consoleUiPath}\\appsettings.json' and environment variables.");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(cs)
            .Options;
        return new AppDbContext(options);
    }
}
