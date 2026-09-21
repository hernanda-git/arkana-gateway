using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Arkana.Infrastructure.Tests.AI;

public sealed class CLIProxyApiManagementContractTests
{
    [Fact]
    public async Task Each_slot_exposes_exactly_one_enabled_auth_record()
    {
        if (!ContractSlots.IsConfigured)
        {
            return;
        }

        var slots = ContractSlots.Load();

        foreach (var slot in slots)
        {
            using var client = slot.CreateClient();
            using var response = await client.GetAsync("v0/management/auth-files");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            document.RootElement.TryGetProperty("files", out var files).Should().BeTrue();
            files.ValueKind.Should().Be(JsonValueKind.Array);

            var enabled = files.EnumerateArray()
                .Where(file => !file.TryGetProperty("disabled", out var disabled) || !disabled.GetBoolean())
                .ToList();
            enabled.Should().HaveCount(1, $"slot {slot.Name} must contain exactly one enabled auth record");

            enabled[0].TryGetProperty("auth_index", out var authIndex).Should().BeTrue();
            authIndex.GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task Management_authentication_is_required()
    {
        if (!ContractSlots.IsConfigured)
        {
            return;
        }

        var slots = ContractSlots.Load();

        foreach (var slot in slots)
        {
            using var client = new HttpClient { BaseAddress = slot.BaseUri };
            using var response = await client.GetAsync("v0/management/auth-files");
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }

    private sealed record ContractSlot(string Name, Uri BaseUri, string ManagementKey)
    {
        public HttpClient CreateClient()
        {
            var client = new HttpClient { BaseAddress = BaseUri };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ManagementKey);
            return client;
        }
    }

    private static class ContractSlots
    {
        public static bool IsConfigured =>
            IsConfiguredFor("A") && IsConfiguredFor("B");

        public static IReadOnlyList<ContractSlot> Load()
        {
            var slotA = Load("A");
            var slotB = Load("B");
            if (slotA is null || slotB is null)
            {
                throw new InvalidOperationException("Set CLIPROXY_CONTRACT_SLOT_A_URL/B_URL and CLIPROXY_CONTRACT_MANAGEMENT_KEY_A/B to run the two-slot broker contract.");
            }

            return [slotA, slotB];
        }

        private static bool IsConfiguredFor(string suffix) =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable($"CLIPROXY_CONTRACT_SLOT_{suffix}_URL")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable($"CLIPROXY_CONTRACT_MANAGEMENT_KEY_{suffix}"));

        private static ContractSlot? Load(string suffix)
        {
            var url = Environment.GetEnvironmentVariable($"CLIPROXY_CONTRACT_SLOT_{suffix}_URL");
            var key = Environment.GetEnvironmentVariable($"CLIPROXY_CONTRACT_MANAGEMENT_KEY_{suffix}");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException($"CLIPROXY_CONTRACT_SLOT_{suffix}_URL must be an absolute URI.");
            }

            return new ContractSlot(suffix, new Uri(url.TrimEnd('/') + "/"), key);
        }
    }
}
