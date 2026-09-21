using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Arkana.Infrastructure;

/// <summary>Design-time factory for EF Core migrations.</summary>
public sealed class GatewayDbContextFactory : IDesignTimeDbContextFactory<GatewayDbContext>
{
    public GatewayDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? Environment.GetEnvironmentVariable("ARKANA_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "A design-time database connection must be supplied through an environment variable.");

        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new GatewayDbContext(options);
    }
}