using Arkana.Infrastructure.AI;

namespace Arkana.Infrastructure.Broker;

/// <summary>
/// Cross-checks the gemini-subscription wiring, which spans two configuration sections and two
/// containers (the gateway and each broker slot). Every problem below previously surfaced only as a
/// runtime failure — most of them as an opaque
/// <c>503 "Gemini subscription streaming failed."</c> on the streaming path while non-streaming kept
/// working — so they are reported once at startup together with the configuration key to fix.
/// </summary>
public static class GeminiBrokerConfigValidation
{
    public static IReadOnlyList<string> Validate(
        GeminiSubscriptionOptions subscription,
        CLIProxyManagementOptions broker,
        string? rawProviderId = null)
    {
        var problems = new List<string>();

        // A configured-but-unparseable value (usually a compose variable that interpolated to "")
        // is the worst case: the feature is unusable and the value looks configured.
        if (!string.IsNullOrWhiteSpace(rawProviderId) && !Guid.TryParse(rawProviderId, out _))
            problems.Add(
                $"GeminiSubscription:ProviderId is not a valid GUID (value '{Truncate(rawProviderId)}'): " +
                "the gemini-subscription feature is disabled. Set it to the AiProviders.Id of the " +
                "gemini-subscription provider, or remove the key entirely.");

        if (broker.Slots.Count > 0 && subscription.ProviderId == Guid.Empty && problems.Count == 0)
            problems.Add(
                "GeminiSubscription:ProviderId is not set while " + broker.Slots.Count +
                " broker slot(s) are configured: gemini-subscription answers 503 'routing is not configured' " +
                "on both streaming and non-streaming before the broker is ever dialed. Set " +
                "GeminiSubscription__ProviderId=<AiProviders.Id of the gemini-subscription provider>.");

        if (broker.Slots.Count == 0 && subscription.ProviderId != Guid.Empty)
            problems.Add(
                "GeminiSubscription:ProviderId is set but no GeminiBroker:Slots are configured: gemini-subscription " +
                "account selection will reject every account. Configure GeminiBroker__Slots__<slot>__BaseUrl " +
                "(and __ManagementKey).");

        foreach (var (name, slot) in broker.Slots)
        {
            if (string.IsNullOrWhiteSpace(slot.BaseUrl) ||
                !Uri.TryCreate(slot.BaseUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                problems.Add(
                    $"GeminiBroker:Slots:{name}:BaseUrl is missing or not an absolute http(s) URL: every " +
                    $"gemini-acc* account bound to slot '{name}' will fail with 'Broker slot is not allowlisted.'");
            }

            if (string.IsNullOrWhiteSpace(slot.ManagementKey) && string.IsNullOrWhiteSpace(slot.DataPlaneKey))
                problems.Add(
                    $"GeminiBroker:Slots:{name} has neither ManagementKey nor DataPlaneKey: account management for " +
                    $"slot '{name}' cannot authenticate to the broker, and a broker that enforces api-keys will " +
                    "reject completions with 401.");
        }

        if (!string.IsNullOrWhiteSpace(broker.DefaultSlot) && broker.Slots.Count > 0 &&
            !broker.Slots.ContainsKey(broker.DefaultSlot))
        {
            problems.Add(
                $"GeminiBroker:DefaultSlot '{broker.DefaultSlot}' is not present in GeminiBroker:Slots " +
                $"({string.Join(", ", broker.Slots.Keys)}): accounts without an explicit slot resolve to a slot " +
                "that does not exist.");
        }

        return problems;
    }

    /// <summary>Keeps a bad configuration value out of the warning verbatim (bounded, no newlines).</summary>
    private static string Truncate(string value)
        => value.Length <= 60 ? value.Replace("\n", " ") : value[..60].Replace("\n", " ") + "…";
}
