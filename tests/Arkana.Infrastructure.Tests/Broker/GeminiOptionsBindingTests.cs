using Arkana.Infrastructure.AI;
using Arkana.Infrastructure.Broker;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Arkana.Infrastructure.Tests.Broker;

/// <summary>
/// Availability regression: an unset compose variable interpolates to an empty string, and the
/// .NET options binder throws on the first read for values it cannot convert (an empty GUID here).
/// That exception escaped out of the host's startup, so the whole gateway crash-looped over one
/// missing configuration value — including on the startup check written to report it.
/// </summary>
public sealed class GeminiOptionsBindingTests
{
    private static IConfiguration Configuration(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    private static ServiceProvider BuildProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddInfrastructureServices("Host=localhost;Database=test");
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Empty_provider_id_disables_the_feature_instead_of_killing_the_host()
    {
        var configuration = Configuration(("GeminiSubscription:ProviderId", ""));
        using var provider = BuildProvider(configuration);

        var options = provider.GetRequiredService<IOptions<GeminiSubscriptionOptions>>();

        options.Invoking(o => _ = o.Value).Should().NotThrow();
        options.Value.ProviderId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void Malformed_broker_values_do_not_throw_on_read()
    {
        var configuration = Configuration(
            ("GeminiSubscription:ProviderId", "not-a-guid"),
            ("GeminiBroker:Timeout", "not-a-timespan"),
            ("GeminiBroker:MaxResponseBytes", "not-a-number"),
            ("GeminiBroker:Slots:gemini-broker-a:BaseUrl", "http://gemini-broker-a:8317"));

        using var provider = BuildProvider(configuration);

        var subscription = provider.GetRequiredService<IOptions<GeminiSubscriptionOptions>>();
        var broker = provider.GetRequiredService<IOptions<CLIProxyManagementOptions>>();

        subscription.Invoking(o => _ = o.Value).Should().NotThrow();
        broker.Invoking(o => _ = o.Value).Should().NotThrow();
        subscription.Value.ProviderId.Should().Be(Guid.Empty);
        broker.Value.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Valid_configuration_still_binds()
    {
        var id = Guid.NewGuid();
        var configuration = Configuration(
            ("GeminiSubscription:ProviderId", id.ToString()),
            ("GeminiBroker:Timeout", "00:02:00"),
            ("GeminiBroker:Slots:gemini-broker-a:BaseUrl", "http://gemini-broker-a:8317"),
            ("GeminiBroker:Slots:gemini-broker-a:ManagementKey", "key"));

        using var provider = BuildProvider(configuration);

        provider.GetRequiredService<IOptions<GeminiSubscriptionOptions>>().Value.ProviderId.Should().Be(id);
        var broker = provider.GetRequiredService<IOptions<CLIProxyManagementOptions>>().Value;
        broker.Timeout.Should().Be(TimeSpan.FromMinutes(2));
        broker.Slots.Should().ContainKey("gemini-broker-a");
    }

    [Fact]
    public void A_malformed_value_is_reported_by_the_startup_check()
    {
        var configuration = Configuration(("GeminiSubscription:ProviderId", ""));
        using var provider = BuildProvider(configuration);

        var problems = GeminiBrokerConfigValidation.Validate(
            provider.GetRequiredService<IOptions<GeminiSubscriptionOptions>>().Value,
            provider.GetRequiredService<IOptions<CLIProxyManagementOptions>>().Value,
            rawProviderId: configuration["GeminiSubscription:ProviderId"]);

        // An empty value means "absent", so the usual not-configured warning applies once slots exist.
        problems.Should().NotContain(p => p.Contains("not a valid GUID"));

        var withSlots = GeminiBrokerConfigValidation.Validate(
            new GeminiSubscriptionOptions { ProviderId = Guid.Empty },
            new CLIProxyManagementOptions
            {
                Slots = new Dictionary<string, CLIProxySlotOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gemini-broker-a"] = new() { BaseUrl = "http://gemini-broker-a:8317", ManagementKey = "k" }
                }
            },
            rawProviderId: "definitely-not-a-guid");

        withSlots.Should().Contain(p => p.Contains("not a valid GUID"));
    }
}
