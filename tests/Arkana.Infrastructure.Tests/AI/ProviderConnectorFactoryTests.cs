using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.Services;
using Arkana.Infrastructure.AI;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.AI;

/// <summary>
/// Pins the behavior of <see cref="ProviderConnectorFactory"/>. The factory
/// is the seam between the DB-backed provider catalog and the compile-time
/// fixed set of registered connectors. These tests are the contract for
/// the factory: any change here ripples to every dialect addition in
/// Phase 3 (Anthropic, Gemini, Groq, OpenRouter, etc.).
/// </summary>
public sealed class ProviderConnectorFactoryTests
{
    private static IChatCompletionService CreateMockProvider(string providerName)
    {
        var mock = Substitute.For<IChatCompletionService>();
        mock.ProviderName.Returns(providerName);
        return mock;
    }

    private static ProviderConnectorFactory CreateFactory(params IChatCompletionService[] connectors)
    {
        return new ProviderConnectorFactory(connectors, NullLogger<ProviderConnectorFactory>.Instance);
    }

    private static AiProvider CreateProvider(string code, string name = "Test")
    {
        // AiProvider.Create requires the vault for envelope encryption; we
        // use a fake vault with a dummy key so the call succeeds.
        var vault = Substitute.For<ICredentialVault>();
        return AiProvider.Create(name, code, priority: 5, apiKeyPlaintext: null, vault: vault);
    }

    // ── ResolveAsync: positive cases ────────────────────────────────

    [Fact]
    public async Task ResolveAsync_returns_connector_for_known_lowercase_code()
    {
        var openCode = CreateMockProvider("OpenCode");
        var factory = CreateFactory(openCode);

        var result = await factory.ResolveAsync(CreateProvider("opencode"));

        result.Should().BeSameAs(openCode);
    }

    [Theory]
    [InlineData("OpenCode", "opencode")]
    [InlineData("OpenAI", "openai")]
    [InlineData("DeepSeek", "deepseek")]
    [InlineData("CLIProxyAPI", "cliproxyapi")]
    public async Task ResolveAsync_matches_each_known_provider_code(string providerName, string providerCode)
    {
        var connector = CreateMockProvider(providerName);
        var factory = CreateFactory(connector);

        var result = await factory.ResolveAsync(CreateProvider(providerCode));

        result.Should().BeSameAs(connector);
    }

    [Fact]
    public async Task ResolveAsync_does_not_route_broker_account_to_direct_cliproxy_connector()
    {
        var cliproxy = CreateMockProvider("CLIProxyAPI");
        var factory = CreateFactory(cliproxy);

        var result = await factory.ResolveAsync(CreateProvider("gemini-acc2"));

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_does_not_route_subscription_alias_to_direct_cliproxy_connector()
    {
        var cliproxy = CreateMockProvider("CLIProxyAPI");
        var factory = CreateFactory(cliproxy);

        var result = await factory.ResolveAsync(CreateProvider("gemini-subscription"));

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_is_case_insensitive_on_provider_code()
    {
        // The seed data uses lowercase codes; an admin could enter
        // mixed case through a future UI. The factory must accept
        // either.
        var openCode = CreateMockProvider("OpenCode");
        var factory = CreateFactory(openCode);

        var result = await factory.ResolveAsync(CreateProvider("OpenCode"));

        result.Should().BeSameAs(openCode);
    }

    // ── ResolveAsync: negative cases ────────────────────────────────

    [Fact]
    public async Task ResolveAsync_returns_null_for_unknown_code()
    {
        var openCode = CreateMockProvider("OpenCode");
        var factory = CreateFactory(openCode);

        var result = await factory.ResolveAsync(CreateProvider("anthropic"));

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_returns_null_for_null_provider()
    {
        var factory = CreateFactory(CreateMockProvider("OpenCode"));

        var result = await factory.ResolveAsync(null!);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_returns_null_for_empty_code()
    {
        var factory = CreateFactory(CreateMockProvider("OpenCode"));

        var result = await factory.ResolveAsync(CreateProvider(""));

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_returns_null_when_no_connectors_registered()
    {
        var factory = CreateFactory();

        var result = await factory.ResolveAsync(CreateProvider("opencode"));

        result.Should().BeNull();
    }

    // ── RegisteredDialectCodes ──────────────────────────────────────

    private static readonly string[] ExpectedKnownCodes =
        ["opencode", "openai", "deepseek", "cliproxyapi"];

    [Fact]
    public void RegisteredDialectCodes_lists_every_registered_provider_in_lowercase()
    {
        var factory = CreateFactory(
            CreateMockProvider("OpenCode"),
            CreateMockProvider("OpenAI"),
            CreateMockProvider("DeepSeek"),
            CreateMockProvider("CLIProxyAPI"));

        var codes = factory.RegisteredDialectCodes;

        codes.Should().BeEquivalentTo(ExpectedKnownCodes);
    }

    [Fact]
    public void RegisteredDialectCodes_is_empty_when_no_connectors()
    {
        var factory = CreateFactory();

        factory.RegisteredDialectCodes.Should().BeEmpty();
    }

    // ── RegisterDialect (alias support) ────────────────────────────

    [Fact]
    public async Task RegisterDialect_adds_extra_code_pointing_at_existing_connector()
    {
        // Scenario: a future Anthropic connector that should serve
        // multiple catalog codes (different models sold as different
        // provider rows). The factory must allow that without duplicating
        // the connector instance.
        var anthropic = CreateMockProvider("Anthropic");
        var factory = CreateFactory(anthropic);
        factory.RegisterDialect("claude-3-opus", anthropic);
        factory.RegisterDialect("claude-3-sonnet", anthropic);

        var opus = await factory.ResolveAsync(CreateProvider("claude-3-opus"));
        var sonnet = await factory.ResolveAsync(CreateProvider("claude-3-sonnet"));

        opus.Should().BeSameAs(anthropic);
        sonnet.Should().BeSameAs(anthropic);
    }

    [Fact]
    public async Task RegisterDialect_overrides_default_code_to_connector_mapping()
    {
        // Last registration wins for the same code.
        var openAi = CreateMockProvider("OpenAI");
        var customOpenAi = CreateMockProvider("OpenAI");
        var factory = CreateFactory(openAi);
        factory.RegisterDialect("openai", customOpenAi);

        var result = await factory.ResolveAsync(CreateProvider("openai"));

        result.Should().BeSameAs(customOpenAi);
    }

    [Theory]
    [InlineData(null, "OpenCode")]
    [InlineData("opencode", null)]
    public void RegisterDialect_throws_for_invalid_arguments(string? code, string? connectorName)
    {
        var factory = CreateFactory();
        var connector = connectorName is null ? null : CreateMockProvider(connectorName);

        Action act = () => factory.RegisterDialect(code!, connector!);

        act.Should().Throw<ArgumentException>();
    }

    // ── Duplicate connector guard ──────────────────────────────────

    [Fact]
    public async Task Constructor_keeps_first_registration_when_two_connectors_share_provider_name()
    {
        // If a future refactor accidentally registers the same provider
        // name twice, the first wins. Loud-log the duplicate so it shows
        // up in startup logs, but never crash.
        var first = CreateMockProvider("OpenCode");
        var second = CreateMockProvider("OpenCode");
        var factory = CreateFactory(first, second);

        var result = await factory.ResolveAsync(CreateProvider("opencode"));

        result.Should().BeSameAs(first);
    }
}
