using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.AI;

public sealed class ModelRouterTests
{
    private static IChatCompletionService CreateMockProvider(string providerName)
    {
        var mock = Substitute.For<IChatCompletionService>();
        mock.ProviderName.Returns(providerName);
        return mock;
    }

    private static ModelRouter CreateRouter(IEnumerable<IChatCompletionService> providers)
    {
        var logger = NullLogger<ModelRouter>.Instance;
        return new ModelRouter(providers, logger);
    }

    [Fact]
    public async Task ResolveAsync_WithoutPreferred_Should_ReturnP0()
    {
        // Arrange
        var openCode = CreateMockProvider("OpenCode");
        var openAi = CreateMockProvider("OpenAI");
        var router = CreateRouter([openAi, openCode]);

        // Act
        var resolved = await router.ResolveAsync(ct: CancellationToken.None);

        // Assert
        resolved.Should().BeSameAs(openCode);
        resolved.ProviderName.Should().Be("OpenCode");
    }

    [Fact]
    public async Task ResolveAsync_WithPreferredProvider_Should_ReturnMatchingProvider()
    {
        // Arrange
        var openCode = CreateMockProvider("OpenCode");
        var gemini = CreateMockProvider("Gemini");
        var router = CreateRouter([openCode, gemini]);

        // Act
        var resolved = await router.ResolveAsync("Gemini", CancellationToken.None);

        // Assert
        resolved.Should().BeSameAs(gemini);
        resolved.ProviderName.Should().Be("Gemini");
    }

    [Fact]
    public async Task ResolveAsync_WithPreferredProvider_Should_BeCaseInsensitive()
    {
        // Arrange
        var openCode = CreateMockProvider("OpenCode");
        var openAi = CreateMockProvider("OpenAI");
        var router = CreateRouter([openCode, openAi]);

        // Act
        var resolved = await router.ResolveAsync("opencode", CancellationToken.None);

        // Assert
        resolved.Should().BeSameAs(openCode);
    }

    [Fact]
    public async Task ResolveAsync_WithUnknownPreferredProvider_Should_ReturnP0()
    {
        // Arrange
        var openCode = CreateMockProvider("OpenCode");
        var openAi = CreateMockProvider("OpenAI");
        var router = CreateRouter([openCode, openAi]);

        // Act
        var resolved = await router.ResolveAsync("NonExistentProvider",
            CancellationToken.None);

        // Assert
        resolved.Should().BeSameAs(openCode);
    }

    [Fact]
    public async Task ResolveAsync_WithNullPreferred_Should_ReturnP0()
    {
        // Arrange
        var openCode = CreateMockProvider("OpenCode");
        var openAi = CreateMockProvider("OpenAI");
        var router = CreateRouter([openCode, openAi]);

        // Act
        var resolved = await router.ResolveAsync(null, CancellationToken.None);

        // Assert
        resolved.Should().BeSameAs(openCode);
    }

    [Fact]
    public async Task ResolveAsync_WithEmptyStringPreferred_Should_ReturnP0()
    {
        // Arrange
        var openCode = CreateMockProvider("OpenCode");
        var openAi = CreateMockProvider("OpenAI");
        var router = CreateRouter([openCode, openAi]);

        // Act
        var resolved = await router.ResolveAsync("", CancellationToken.None);

        // Assert
        resolved.Should().BeSameAs(openCode);
    }

    [Fact]
    public async Task ResolveAsync_Should_OrderProvidersCorrectly()
    {
        // Arrange
        var ollama = CreateMockProvider("Ollama");
        var openAi = CreateMockProvider("OpenAI");
        var unknown = CreateMockProvider("UnknownProvider");
        var openCode = CreateMockProvider("OpenCode");

        var router = CreateRouter([ollama, openAi, unknown, openCode]);

        // Act — P0 should be OpenCode (priority 0)
        var resolved = await router.ResolveAsync(ct: CancellationToken.None);

        // Assert
        resolved.Should().BeSameAs(openCode);
    }

    [Fact]
    public async Task GetAllProvidersAsync_Should_ReturnAllProviders()
    {
        // Arrange
        var openCode = CreateMockProvider("OpenCode");
        var openAi = CreateMockProvider("OpenAI");
        var gemini = CreateMockProvider("Gemini");
        var router = CreateRouter([openCode, openAi, gemini]);

        // Act
        var providers = await router.GetAllProvidersAsync(CancellationToken.None);

        // Assert
        providers.Should().HaveCount(3);
        providers.Should().ContainInOrder(openCode, openAi, gemini);
    }

    [Fact]
    public async Task GetAllProvidersAsync_Should_ReturnAllProvidersInOrder()
    {
        // Arrange — all 5 known provider types in shuffled order
        var ollama = CreateMockProvider("Ollama");
        var gemini = CreateMockProvider("Gemini");
        var openAi = CreateMockProvider("OpenAI");
        var anthropic = CreateMockProvider("Anthropic");
        var openCode = CreateMockProvider("OpenCode");
        var router = CreateRouter([ollama, gemini, openAi, anthropic, openCode]);

        // Act
        var providers = await router.GetAllProvidersAsync(CancellationToken.None);

        // Assert — order should be: OpenCode(0), OpenAI(1), Gemini(2), Anthropic(3), Ollama(4)
        providers.Should().HaveCount(5);
        providers.Should().ContainInOrder(openCode, openAi, gemini, anthropic, ollama);
    }

    [Fact]
    public async Task ResolveAsync_WithAnthropicPreferred_Should_ReturnAnthropic()
    {
        // Arrange
        var openCode = CreateMockProvider("OpenCode");
        var anthropic = CreateMockProvider("Anthropic");
        var router = CreateRouter([openCode, anthropic]);

        // Act
        var resolved = await router.ResolveAsync("Anthropic", CancellationToken.None);

        // Assert
        resolved.Should().BeSameAs(anthropic);
        resolved.ProviderName.Should().Be("Anthropic");
    }

    [Fact]
    public async Task ResolveAsync_WithOllamaPreferred_Should_ReturnOllama()
    {
        // Arrange
        var openCode = CreateMockProvider("OpenCode");
        var ollama = CreateMockProvider("Ollama");
        var router = CreateRouter([openCode, ollama]);

        // Act
        var resolved = await router.ResolveAsync("Ollama", CancellationToken.None);

        // Assert
        resolved.Should().BeSameAs(ollama);
        resolved.ProviderName.Should().Be("Ollama");
    }

    [Fact]
    public async Task GetAllProvidersAsync_Should_ReturnEmpty_WhenNoProviders()
    {
        // Arrange
        var router = CreateRouter(Enumerable.Empty<IChatCompletionService>());

        // Act
        var providers = await router.GetAllProvidersAsync(CancellationToken.None);

        // Assert
        providers.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_Should_Throw_WhenNoProviders()
    {
        // Arrange
        var router = CreateRouter(Enumerable.Empty<IChatCompletionService>());

        // Act
        Func<Task> act = () => router.ResolveAsync(ct: CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Constructor_Should_MaterializeProvidersImmediately()
    {
        // Arrange
        var openCode = CreateMockProvider("OpenCode");
        var openAi = CreateMockProvider("OpenAI");

        // Use a lazy enumerable to verify it's consumed in ctor
        var lazyProviders = new List<IChatCompletionService> { openCode, openAi }.AsEnumerable();

        var router = CreateRouter(lazyProviders);

        // Act
        var providers = await router.GetAllProvidersAsync(CancellationToken.None);

        // Assert
        providers.Should().HaveCount(2);
    }
}
