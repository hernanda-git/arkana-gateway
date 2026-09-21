using Arkana.Infrastructure.AI;
using Arkana.Infrastructure.Broker;
using FluentAssertions;

namespace Arkana.Infrastructure.Tests.Broker;

public sealed class GeminiBrokerConfigValidationTests
{
    private static CLIProxyManagementOptions Slots(params (string Name, CLIProxySlotOptions Slot)[] slots) =>
        new()
        {
            Slots = slots.ToDictionary(s => s.Name, s => s.Slot, StringComparer.OrdinalIgnoreCase)
        };

    private static CLIProxySlotOptions Healthy(string name = "gemini-broker-a") =>
        new() { BaseUrl = $"http://{name}:8317", ManagementKey = "key" };

    [Fact]
    public void Reports_missing_provider_id_while_slots_are_configured()
    {
        // The live regression: slots wired, ProviderId lost from the compose env, so every
        // gemini-subscription stream answered 503 "routing is not configured" before dialing
        // the broker while non-streaming kept working.
        var problems = GeminiBrokerConfigValidation.Validate(
            new GeminiSubscriptionOptions { ProviderId = Guid.Empty },
            Slots(("gemini-broker-a", Healthy())));

        problems.Should().ContainSingle();
        problems[0].Should().Contain("GeminiSubscription__ProviderId");
        problems[0].Should().Contain("503");
    }

    [Fact]
    public void Reports_provider_id_without_any_slots()
    {
        var problems = GeminiBrokerConfigValidation.Validate(
            new GeminiSubscriptionOptions { ProviderId = Guid.NewGuid() },
            new CLIProxyManagementOptions());

        problems.Should().ContainSingle();
        problems[0].Should().Contain("no GeminiBroker:Slots");
    }

    [Theory]
    [InlineData("")]
    [InlineData("gemini-broker-a:8317")]
    [InlineData("ftp://gemini-broker-a")]
    public void Reports_unusable_slot_base_url(string baseUrl)
    {
        var slot = new CLIProxySlotOptions { BaseUrl = baseUrl, ManagementKey = "key" };

        var problems = GeminiBrokerConfigValidation.Validate(
            new GeminiSubscriptionOptions { ProviderId = Guid.NewGuid() },
            Slots(("gemini-broker-b", slot)));

        problems.Should().ContainSingle(p => p.Contains("BaseUrl") && p.Contains("gemini-broker-b"));
    }

    [Fact]
    public void Reports_a_slot_without_any_key()
    {
        var slot = new CLIProxySlotOptions { BaseUrl = "http://gemini-broker-b:8317" };

        var problems = GeminiBrokerConfigValidation.Validate(
            new GeminiSubscriptionOptions { ProviderId = Guid.NewGuid() },
            Slots(("gemini-broker-b", slot)));

        problems.Should().ContainSingle(p => p.Contains("ManagementKey") && p.Contains("DataPlaneKey"));
    }

    [Fact]
    public void Reports_a_default_slot_that_does_not_exist()
    {
        var options = Slots(("gemini-broker-b", Healthy("gemini-broker-b")));
        options.DefaultSlot = "gemini-broker-a";

        var problems = GeminiBrokerConfigValidation.Validate(
            new GeminiSubscriptionOptions { ProviderId = Guid.NewGuid() }, options);

        problems.Should().ContainSingle(p => p.Contains("DefaultSlot") && p.Contains("gemini-broker-a"));
    }

    [Fact]
    public void Healthy_wiring_reports_nothing()
    {
        var problems = GeminiBrokerConfigValidation.Validate(
            new GeminiSubscriptionOptions { ProviderId = Guid.NewGuid() },
            Slots(("gemini-broker-a", Healthy()), ("gemini-broker-b", Healthy("gemini-broker-b"))));

        problems.Should().BeEmpty();
    }

    [Fact]
    public void Empty_configuration_is_valid_for_a_gateway_without_the_broker_feature()
    {
        // A fresh clone with no broker configured must stay silent: the feature is optional.
        GeminiBrokerConfigValidation
            .Validate(new GeminiSubscriptionOptions(), new CLIProxyManagementOptions())
            .Should().BeEmpty();
    }
}
