using Arkana.Domain.Entities;
using FluentAssertions;

namespace Arkana.Domain.Tests.Entities;

public sealed class ModelTests
{
    [Fact]
    public void Create_SetsAllPropertiesCorrectly()
    {
        var providerId = Guid.NewGuid();
        var model = Model.Create(providerId, "GPT-4o", "gpt-4o",
            costPerInput: 0.01m, costPerOutput: 0.03m, maxTokens: 8192);

        model.ProviderId.Should().Be(providerId);
        model.Name.Should().Be("GPT-4o");
        model.Code.Should().Be("gpt-4o");
        model.CostPerInputToken.Should().Be(0.01m);
        model.CostPerOutputToken.Should().Be(0.03m);
        model.MaxTokensPerRequest.Should().Be(8192);
        model.IsEnabled.Should().BeTrue();
        model.Id.Should().NotBeEmpty();
        model.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Enable_SetsIsEnabledToTrue()
    {
        var model = Model.Create(Guid.NewGuid(), "Model", "model");
        model.Disable();

        model.Enable();

        model.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Disable_SetsIsEnabledToFalse()
    {
        var model = Model.Create(Guid.NewGuid(), "Model", "model");

        model.Disable();

        model.IsEnabled.Should().BeFalse();
    }
}
