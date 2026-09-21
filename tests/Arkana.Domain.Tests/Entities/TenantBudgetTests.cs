using Arkana.Domain.Entities;

namespace Arkana.Domain.Tests.Entities;

public sealed class TenantBudgetTests
{
    [Fact]
    public void Create_ShouldSetCapsAndRemaining()
    {
        var tenant = Tenant.Create("Test", "test");
        var budget = TenantBudget.Create(tenant, 1_000_000, 500_000);

        budget.TenantId.Should().Be(tenant.Id);
        budget.MonthlyInputTokenCap.Should().Be(1_000_000);
        budget.MonthlyOutputTokenCap.Should().Be(500_000);
        budget.RemainingInputTokens.Should().Be(1_000_000);
        budget.RemainingOutputTokens.Should().Be(500_000);
    }

    [Fact]
    public void Create_ShouldSetPeriodToCurrentMonth()
    {
        var tenant = Tenant.Create("Test", "test");
        var budget = TenantBudget.Create(tenant, 1000, 500);

        budget.PeriodStart.Day.Should().Be(1);
        budget.PeriodEnd.Day.Should().Be(1);
        budget.PeriodEnd.Month.Should().Be(budget.PeriodStart.Month + 1);
    }

    [Fact]
    public void UpdateCaps_ShouldResetRemaining()
    {
        var tenant = Tenant.Create("Test", "test");
        var budget = TenantBudget.Create(tenant, 1000, 500);

        budget.UpdateCaps(2000, 1000);

        budget.MonthlyInputTokenCap.Should().Be(2000);
        budget.MonthlyOutputTokenCap.Should().Be(1000);
        budget.RemainingInputTokens.Should().Be(2000);
        budget.RemainingOutputTokens.Should().Be(1000);
    }

    [Fact]
    public void ResetPeriod_ShouldRefreshRemaining()
    {
        var tenant = Tenant.Create("Test", "test");
        var budget = TenantBudget.Create(tenant, 1000, 500);

        // Simulate consumption (RowVersion field is normally only set by DB)
        var remainingField = typeof(TenantBudget).GetProperty(nameof(TenantBudget.RemainingInputTokens));
        remainingField?.SetValue(budget, 500L);

        var newStart = DateTimeOffset.UtcNow;
        var newEnd = newStart.AddMonths(1);
        budget.ResetPeriod(newStart, newEnd);

        budget.RemainingInputTokens.Should().Be(1000);
        budget.RemainingOutputTokens.Should().Be(500);
        budget.PeriodStart.Should().Be(newStart);
        budget.PeriodEnd.Should().Be(newEnd);
    }

    [Fact]
    public void UsageRatio_ShouldReflectConsumption()
    {
        var tenant = Tenant.Create("Test", "test");
        var budget = TenantBudget.Create(tenant, 1000, 500);

        var remainingField = typeof(TenantBudget).GetProperty(nameof(TenantBudget.RemainingInputTokens));
        remainingField?.SetValue(budget, 600L);

        budget.UsageRatio.Should().BeApproximately(0.4, 0.001);
    }

    [Fact]
    public void UsageRatio_ShouldBeZero_WhenNoCap()
    {
        var tenant = Tenant.Create("Test", "test");
        var budget = TenantBudget.Create(tenant, 0, 0);

        budget.UsageRatio.Should().Be(0.0);
    }
}
