using Arkana.Domain.Entities;

namespace Arkana.Domain.Tests.Entities;

public sealed class PlanTests
{
    [Fact]
    public void Create_ShouldSetProperties()
    {
        var plan = Plan.Create("Pro", "pro", 29.99m, 1_000_000, 500_000);

        plan.Id.Should().NotBeEmpty();
        plan.Name.Should().Be("Pro");
        plan.Slug.Should().Be("pro");
        plan.MonthlyPrice.Should().Be(29.99m);
        plan.IncludedInputTokens.Should().Be(1_000_000);
        plan.IncludedOutputTokens.Should().Be(500_000);
        plan.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Create_WithDefaults_ShouldSetSensibleValues()
    {
        var plan = Plan.Create("Free", "free", 0m, 0, 0);

        plan.MaxRequestsPerMinute.Should().Be(60);
        plan.MaxTokensPerMinute.Should().Be(1_000_000);
        plan.MaxConcurrent.Should().Be(5);
        plan.MaxApiKeys.Should().Be(10);
    }

    [Fact]
    public void Create_WithFeatures_ShouldStore()
    {
        var features = "{\"semantic_cache\":true,\"streaming\":true}";
        var plan = Plan.Create("Pro", "pro", 29.99m, 1_000_000, 500_000, features: features);

        plan.Features.Should().Be(features);
    }
}

public sealed class TenantPlanTests
{
    private static readonly Tenant Tenant = Tenant.Create("Test", "test");
    private static readonly Plan Plan = Plan.Create("Pro", "pro", 29.99m, 1_000_000, 500_000,
        rpm: 60, tpm: 1_000_000, concurrent: 5, maxKeys: 10);

    [Fact]
    public void Assign_ShouldCreateLink()
    {
        var tp = TenantPlan.Assign(Tenant, Plan);

        tp.TenantId.Should().Be(Tenant.Id);
        tp.Tenant.Should().Be(Tenant);
        tp.PlanId.Should().Be(Plan.Id);
        tp.Plan.Should().Be(Plan);
        tp.EndsAt.Should().BeNull();
        tp.HasActivePlan.Should().BeTrue();
    }

    [Fact]
    public void Assign_WithEndDate_ShouldExpire()
    {
        var tp = TenantPlan.Assign(Tenant, Plan, DateTimeOffset.UtcNow.AddDays(-1));

        tp.HasActivePlan.Should().BeFalse();
    }

    [Fact]
    public void EffectiveValues_ShouldUsePlanDefaults_WhenNoOverrides()
    {
        var tp = TenantPlan.Assign(Tenant, Plan);

        tp.EffectiveInputTokens.Should().Be(Plan.IncludedInputTokens);
        tp.EffectiveOutputTokens.Should().Be(Plan.IncludedOutputTokens);
        tp.EffectiveRpm.Should().Be(Plan.MaxRequestsPerMinute);
        tp.EffectiveTpm.Should().Be(Plan.MaxTokensPerMinute);
        tp.EffectiveMaxConcurrent.Should().Be(Plan.MaxConcurrent);
        tp.EffectiveMaxApiKeys.Should().Be(Plan.MaxApiKeys);
    }

    [Fact]
    public void EffectiveValues_ShouldUseOverride_WhenSet()
    {
        var tp = TenantPlan.Assign(Tenant, Plan);
        // Simulate override via reflection (private setter is EF Core only)
        var field = typeof(TenantPlan).GetProperty(nameof(TenantPlan.OverrideIncludedInputTokens));
        field?.SetValue(tp, 5_000_000L);

        tp.EffectiveInputTokens.Should().Be(5_000_000);
        tp.EffectiveOutputTokens.Should().Be(Plan.IncludedOutputTokens); // unchanged
    }
}
