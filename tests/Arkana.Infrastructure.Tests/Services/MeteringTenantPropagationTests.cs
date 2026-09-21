using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Entities;
using Arkana.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.Services;

public sealed class MeteringTenantPropagationTests
{
    [Fact]
    public async Task BatchedUsage_StampsTenant_AndWriterPersistsIt()
    {
        var tenantId = Guid.NewGuid();
        var (connection, options) = CreateRelationalDatabase();
        using var _ = connection;
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(tenantId);
        var queue = new BatchedMeteringQueue(
            Options.Create(new MeteringQueueOptions()),
            NullLogger<BatchedMeteringQueue>.Instance);
        var factory = new FixedDbContextFactory(options);
        var tracker = new BatchedTokenTracker(queue, factory, tenant);

        await tracker.RecordUsageAsync(new TokenUsage(
            "opencode", "model", 10, 5, 0.001m, TimeSpan.FromMilliseconds(4), "same-name"));
        var batch = queue.Drain(10);
        var services = new ServiceCollection().BuildServiceProvider();
        var writer = new MeteringDbWriter(
            factory,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MeteringDbWriter>.Instance);

        await writer.WriteAsync(batch);

        await using var verify = new GatewayDbContext(options);
        var row = await verify.TokenUsages.SingleAsync();
        row.TenantId.Should().Be(tenantId);
        row.ApiKeyName.Should().Be("same-name");
    }

    private static (SqliteConnection Connection, DbContextOptions<GatewayDbContext> Options) CreateRelationalDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite(connection)
            .Options;
        using var context = new GatewayDbContext(options);
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel())
            .Where(operation => operation is CreateTableOperation table
                && table.Name == "TokenUsages")
            .ToList();
        foreach (var command in context.GetService<IMigrationsSqlGenerator>().Generate(operations))
            context.Database.ExecuteSqlRaw(command.CommandText);
        return (connection, options);
    }

    private sealed class FixedDbContextFactory : IDbContextFactory<GatewayDbContext>
    {
        private readonly DbContextOptions<GatewayDbContext> _options;

        public FixedDbContextFactory(DbContextOptions<GatewayDbContext> options)
            => _options = options;

        public GatewayDbContext CreateDbContext()
            => new(_options);

        public Task<GatewayDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GatewayDbContext(_options));
    }
}
