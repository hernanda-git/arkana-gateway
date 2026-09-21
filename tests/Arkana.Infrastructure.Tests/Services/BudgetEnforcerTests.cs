using Arkana.Domain.Services;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arkana.Infrastructure.Tests.Services;

/// <summary>
/// Stub factory that throws on use — only suitable for testing
/// IsEnabled (which never touches the DB).
/// </summary>
internal sealed class StubDbContextFactory : IDbContextFactory<GatewayDbContext>
{
    public GatewayDbContext CreateDbContext() =>
        throw new NotSupportedException("Not expected in this test");

    public Task<GatewayDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("Not expected in this test");
}

public sealed class BudgetEnforcerTests
{
    private static readonly IDbContextFactory<GatewayDbContext> StubFactory = new StubDbContextFactory();

    [Fact]
    public void IsEnabled_Default_ShouldBeFalse()
    {
        var enforcer = new BudgetEnforcer(StubFactory, NullLogger<BudgetEnforcer>.Instance);

        enforcer.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_True_WhenExplicitlySet()
    {
        var enforcer = new BudgetEnforcer(StubFactory, NullLogger<BudgetEnforcer>.Instance, enabled: true);

        enforcer.IsEnabled.Should().BeTrue();
    }
}
