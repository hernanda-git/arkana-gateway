using Arkana.Domain.Entities;

namespace Arkana.Domain.Tests.Entities;

public sealed class TenantTests
{
    [Fact]
    public void Create_ShouldSetProperties()
    {
        var tenant = Tenant.Create("Acme Corp", "acme-corp");

        tenant.Id.Should().NotBeEmpty();
        tenant.Name.Should().Be("Acme Corp");
        tenant.Slug.Should().Be("acme-corp");
        tenant.IsActive.Should().BeTrue();
        tenant.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Create_ShouldLowercaseSlug()
    {
        var tenant = Tenant.Create("ACME Corp", "ACME-CORP");

        tenant.Slug.Should().Be("acme-corp");
    }

    [Fact]
    public void Create_WithSettings_ShouldStore()
    {
        var tenant = Tenant.Create("Test", "test", "{\"region\":\"us-east\"}");

        tenant.Settings.Should().Be("{\"region\":\"us-east\"}");
    }

    [Fact]
    public void Activate_ShouldSetIsActiveTrue()
    {
        var tenant = Tenant.Create("Test", "test");
        tenant.Deactivate();
        tenant.IsActive.Should().BeFalse();

        tenant.Activate();

        tenant.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Deactivate_ShouldSetIsActiveFalse()
    {
        var tenant = Tenant.Create("Test", "test");

        tenant.Deactivate();

        tenant.IsActive.Should().BeFalse();
    }
}
