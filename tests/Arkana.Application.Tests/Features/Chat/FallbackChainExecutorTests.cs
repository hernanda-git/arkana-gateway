using Arkana.Application.Features.Chat;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Arkana.Application.Tests.Features.Chat;

public sealed class FallbackChainExecutorTests
{
    // Use the Test policy (1 attempt per provider) so tests are deterministic
    // and don't wait on real backoff. The retry-with-backoff behavior is
    // covered by RetryPolicyTests.
    private readonly FallbackChainExecutor _sut = new(
        Substitute.For<ILogger<FallbackChainExecutor>>(),
        RetryPolicy.Test);

    private static IChatCompletionService MockService(
        string name,
        ChatResult? result = null,
        Exception? throws = null)
    {
        var svc = Substitute.For<IChatCompletionService>();
        svc.ProviderName.Returns(name);

        if (throws is not null)
        {
            svc.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
                .Returns<Task<ChatResult>>(_ => throw throws);
        }
        else
        {
            svc.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
                .Returns(result ?? new ChatResult
                {
                    Content = $"ok from {name}",
                    Model = "gpt-4",
                    InputTokens = 5,
                    OutputTokens = 5
                });
        }
        return svc;
    }

    [Fact]
    public async Task ExecuteAsync_PrimarySucceeds_NoFallback()
    {
        var primary = MockService("OpenAI", new ChatResult
        {
            Content = "primary", Model = "gpt-4"
        });
        var fallback = MockService("DeepSeek");

        var exec = await _sut.ExecuteAsync([primary, fallback], new ChatRequest { Model = "gpt-4" });

        exec.Result.IsSuccess.Should().BeTrue();
        exec.Result.Content.Should().Be("primary");
        exec.ServedBy.Should().Be(primary);
        exec.Attempts.Should().Be(1);
        await fallback.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_PrimaryFailsRetryable_FallsToSecondary()
    {
        var primary = MockService("OpenAI", new ChatResult
        {
            ErrorMessage = "Internal Server Error", Model = "gpt-4"
        });
        var fallback = MockService("DeepSeek", new ChatResult
        {
            Content = "from fallback", Model = "gpt-4"
        });

        var exec = await _sut.ExecuteAsync([primary, fallback], new ChatRequest { Model = "gpt-4" });

        exec.Result.IsSuccess.Should().BeTrue();
        exec.Result.Content.Should().Be("from fallback");
        exec.ServedBy.Should().Be(fallback);
        // RetryPolicy.Test = 2 attempts per provider. Primary tried 2x,
        // then fallback tried 1x = 3 total attempts.
        exec.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_PrimaryFailsNonRetryable_StopsChain()
    {
        var primary = MockService("OpenAI", new ChatResult
        {
            ErrorMessage = "401 Unauthorized: invalid api key", Model = "gpt-4"
        });
        var fallback = MockService("DeepSeek");

        var exec = await _sut.ExecuteAsync([primary, fallback], new ChatRequest { Model = "gpt-4" });

        exec.Result.IsSuccess.Should().BeFalse();
        exec.ServedBy.Should().Be(primary);
        exec.Attempts.Should().Be(1);
        await fallback.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_AllProvidersFail_ReturnsLastError()
    {
        var p1 = MockService("A", new ChatResult { ErrorMessage = "first error", Model = "gpt-4" });
        var p2 = MockService("B", new ChatResult { ErrorMessage = "second error", Model = "gpt-4" });
        var p3 = MockService("C", new ChatResult { ErrorMessage = "third error", Model = "gpt-4" });

        var exec = await _sut.ExecuteAsync([p1, p2, p3], new ChatRequest { Model = "gpt-4" });

        exec.Result.IsSuccess.Should().BeFalse();
        exec.Result.ErrorMessage.Should().Be("third error");
        exec.ServedBy.Should().Be(p3);
        // 3 providers × 2 attempts each = 6 total attempts
        exec.Attempts.Should().Be(6);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyChain_Throws()
    {
        Func<Task> act = async () => await _sut.ExecuteAsync(
            Array.Empty<IChatCompletionService>(),
            new ChatRequest { Model = "gpt-4" });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("401 Unauthorized", false)]                // auth — terminal
    [InlineData("403 Forbidden", false)]                   // forbidden — terminal
    [InlineData("400 Bad Request: invalid_request", false)] // bad request — terminal
    [InlineData("context_length_exceeded", false)]         // context too long — terminal
    [InlineData("model_not_found", false)]                // bad model — terminal
    [InlineData("insufficient_quota", false)]             // billing — terminal
    [InlineData("content_policy violation", false)]        // content filter — terminal
    [InlineData("Connection timeout", true)]               // network — retryable
    [InlineData("500 Internal Server Error", true)]        // server error — retryable
    [InlineData("429 Too Many Requests", true)]            // rate limit — retryable
    [InlineData("Service Unavailable", true)]              // transient — retryable
    [InlineData("DNS resolution failed", true)]            // network — retryable
    // ── Regression 2026-08-06: real upstream-exhaustion strings ──────
    // These are the VERBATIM messages the gateway produces during an
    // OpenCode outage. Each must fail OVER to the next provider, not
    // terminate the chain.
    [InlineData("OpenCode upstream returned HTTP 429", true)]
    [InlineData("OpenCode: GoUsageLimitError: Weekly usage limit reached. Resets in 3 days", true)]
    // 403 RegionError is terminal for THAT provider but another provider
    // may serve it — previously the bare "403"/"forbidden" substring made
    // this terminal and killed the chain.
    [InlineData("OpenCode upstream returned HTTP 403", false)] // bare 403 stays terminal
    [InlineData("OpenCode: RegionError: only available hosted in China, requires explicit opt in", true)]
    [InlineData("Rate limit exceeded, please retry", true)]
    public void IsRetryable_ClassifiesCorrectly(string error, bool expected)
    {
        var result = new ChatResult { ErrorMessage = error };
        FallbackChainExecutor.IsRetryable(result).Should().Be(expected);
    }

    [Fact]
    public void IsRetryable_SuccessfulResult_ReturnsFalse()
    {
        FallbackChainExecutor.IsRetryable(new ChatResult
        {
            Content = "ok", Model = "gpt-4"
        }).Should().BeFalse();
    }

    // ── BuildChain tests ───────────────────────────────────────

    [Fact]
    public void BuildChain_OrdersByPriority()
    {
        var openai = AiProvider.Create("OpenAI", "openai", 0);
        var deepseek = AiProvider.Create("DeepSeek", "deepseek", 1);
        var ollama = AiProvider.Create("Ollama", "ollama", 2);
        var openaiSvc = MockService("OpenAI");
        var deepseekSvc = MockService("DeepSeek");
        var ollamaSvc = MockService("Ollama");

        var chain = FallbackChainExecutor.BuildChain(
            [openai, deepseek, ollama], primary: openai,
            [openaiSvc, deepseekSvc, ollamaSvc]);

        chain.Should().HaveCount(3);
        chain[0].Should().Be(openaiSvc);
        chain[1].Should().Be(deepseekSvc);
        chain[2].Should().Be(ollamaSvc);
    }

    [Fact]
    public void BuildChain_PromotesPrimaryToFirst()
    {
        // Primary is openai, but priority sort would put it second after
        // a default-zero entry. Explicit primary should be promoted.
        var p0 = AiProvider.Create("Zero", "zero", 0);
        var p1 = AiProvider.Create("OpenAI", "openai", 1);
        var p2 = AiProvider.Create("DeepSeek", "deepseek", 2);

        var s0 = MockService("Zero");
        var s1 = MockService("OpenAI");
        var s2 = MockService("DeepSeek");

        var chain = FallbackChainExecutor.BuildChain(
            [p0, p1, p2], primary: p1, [s0, s1, s2]);

        chain[0].Should().Be(s1); // OpenAI promoted
        chain.Should().HaveCount(3);
    }

    [Fact]
    public void BuildChain_StrictRoutingContainsOnlyPinnedProvider()
    {
        var pinned = AiProvider.Create("Pinned", "pinned", 10);
        var other = AiProvider.Create("Other", "other", 0);
        var pinnedService = MockService("Pinned");
        var otherService = MockService("Other");

        var chain = FallbackChainExecutor.BuildChain(
            [other, pinned], primary: pinned, [otherService, pinnedService],
            allowProviderFallback: false);

        chain.Should().ContainSingle().Which.Should().Be(pinnedService);
    }

    [Fact]
    public void BuildChain_StrictRoutingWithMissingPinReturnsEmpty()
    {
        var provider = AiProvider.Create("Other", "other", 0);
        var service = MockService("Other");

        var chain = FallbackChainExecutor.BuildChain(
            [provider], primary: null, [service], allowProviderFallback: false);

        chain.Should().BeEmpty();
    }

    [Fact]
    public void BuildChain_ExcludesDisabledProviders()
    {
        var enabled = AiProvider.Create("OpenAI", "openai", 0);
        var disabled = AiProvider.Create("DeepSeek", "deepseek", 1);
        disabled.Disable();

        var s1 = MockService("OpenAI");
        var s2 = MockService("DeepSeek");

        var chain = FallbackChainExecutor.BuildChain(
            [enabled, disabled], primary: enabled, [s1, s2]);

        chain.Should().ContainSingle().Which.Should().Be(s1);
    }

    [Fact]
    public void BuildChain_ExcludesProvidersWithoutServices()
    {
        var p1 = AiProvider.Create("OpenAI", "openai", 0);
        var p2 = AiProvider.Create("NoService", "noservice", 1);

        var s1 = MockService("OpenAI");

        var chain = FallbackChainExecutor.BuildChain(
            [p1, p2], primary: p1, [s1]);

        chain.Should().ContainSingle().Which.Should().Be(s1);
    }

    [Fact]
    public void BuildChain_MapsChatGptAccountToSharedConnector()
    {
        var account = AiProvider.Create("Hernanda's Codex", "chatgpt-acc2", 0);
        var chatGpt = MockService("ChatGptCodex");

        var chain = FallbackChainExecutor.BuildChain(
            [account], primary: account, [chatGpt]);

        chain.Should().ContainSingle().Which.Should().Be(chatGpt);
    }

    [Fact]
    public void BuildChain_PinnedGeminiAccount_ExcludesEveryOtherProvider()
    {
        var account1 = AiProvider.Create("Gemini Account 1", "gemini-acc1", 0);
        var account2 = AiProvider.Create("Gemini Account 2", "gemini-acc2", 1);
        var fallback = AiProvider.Create("Ollama", "ollama", 2);
        var gemini = MockService("gemini-subscription");
        var ollama = MockService("Ollama");

        var chain = FallbackChainExecutor.BuildChain(
            [account1, account2, fallback], primary: account1, [gemini, ollama],
            allowProviderFallback: false);

        chain.Should().ContainSingle().Which.Should().Be(gemini);
    }
    [Fact]
    public void BuildChain_EmptyInputs_ReturnsEmpty()
    {
        FallbackChainExecutor.BuildChain(
            Array.Empty<AiProvider>(), null, []).Should().BeEmpty();

        AiProvider.Create("X", "x", 0);
        FallbackChainExecutor.BuildChain(
            new List<AiProvider>(), null, []).Should().BeEmpty();
    }

    [Fact]
    public void BuildChain_AllDisabled_ReturnsEmpty()
    {
        var p1 = AiProvider.Create("A", "a", 0);
        p1.Disable();
        var p2 = AiProvider.Create("B", "b", 1);
        p2.Disable();

        var s1 = MockService("A");

        FallbackChainExecutor.BuildChain([p1, p2], null, [s1]).Should().BeEmpty();
    }

    // ── Cooldown integration tests (REL-ARKANA-003) ─────────────

    private static FallbackChainExecutor NewExecutorWithCooldown(
        ILogger<FallbackChainExecutor>? logger = null,
        CooldownOptions? options = null)
    {
        return new FallbackChainExecutor(
            logger ?? Substitute.For<ILogger<FallbackChainExecutor>>(),
            RetryPolicy.Test,
            new CooldownTracker(options ?? CooldownOptions.Test));
    }

    [Fact]
    public async Task ExecuteAsync_ProviderOnCooldown_IsSkipped()
    {
        // Arrange — manually trip the cooldown for the primary
        var cooldown = new CooldownTracker(CooldownOptions.Test);
        cooldown.RecordFailure("OpenAI");
        cooldown.RecordFailure("OpenAI"); // now on cooldown

        var sut = new FallbackChainExecutor(
            Substitute.For<ILogger<FallbackChainExecutor>>(),
            RetryPolicy.Test,
            cooldown);

        var primary = MockService("OpenAI", new ChatResult
        {
            ErrorMessage = "should not be called", Model = "gpt-4"
        });
        var fallback = MockService("DeepSeek", new ChatResult
        {
            Content = "from fallback", Model = "gpt-4"
        });

        // Act
        var exec = await sut.ExecuteAsync([primary, fallback], new ChatRequest { Model = "gpt-4" });

        // Assert
        exec.Result.IsSuccess.Should().BeTrue();
        exec.ServedBy.Should().Be(fallback);
        await primary.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default);
        await fallback.Received(1).CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RepeatedFailures_OpenCooldown()
    {
        // Use a custom cooldown policy that opens on the 2nd failure
        var cooldown = new CooldownTracker(CooldownOptions.Test); // 2 failures → cooldown
        var sut = new FallbackChainExecutor(
            Substitute.For<ILogger<FallbackChainExecutor>>(),
            RetryPolicy.Test, // 2 attempts per provider
            cooldown);

        var failing = MockService("OpenAI", new ChatResult
        {
            ErrorMessage = "Internal Server Error", Model = "gpt-4"
        });

        // Each executor call records 1 failure (the result of the retry loop).
        // The retry loop's per-attempt failures don't accumulate at this layer.
        await sut.ExecuteAsync([failing], new ChatRequest { Model = "gpt-4" });
        await sut.ExecuteAsync([failing], new ChatRequest { Model = "gpt-4" });
        // 2 failures → cooldown opens
        cooldown.IsOnCooldown("OpenAI").Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ProviderRecovers_CooldownCleared()
    {
        var cooldown = new CooldownTracker(CooldownOptions.Test);
        var sut = new FallbackChainExecutor(
            Substitute.For<ILogger<FallbackChainExecutor>>(),
            RetryPolicy.Test,
            cooldown);

        var primary = Substitute.For<IChatCompletionService>();
        primary.ProviderName.Returns("OpenAI");

        // Two calls with failure → opens cooldown (2 failures)
        primary.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResult { ErrorMessage = "Server Error", Model = "gpt-4" });
        await sut.ExecuteAsync([primary], new ChatRequest { Model = "gpt-4" });
        await sut.ExecuteAsync([primary], new ChatRequest { Model = "gpt-4" });
        cooldown.IsOnCooldown("OpenAI").Should().NotBeNull();

        // Wait for cooldown to expire (Test duration is 1s)
        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        // Cooldown should be cleared on the next IsOnCooldown call
        cooldown.IsOnCooldown("OpenAI").Should().BeNull();

        // Now the call should succeed
        primary.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResult { Content = "ok", Model = "gpt-4" });
        var exec = await sut.ExecuteAsync([primary], new ChatRequest { Model = "gpt-4" });

        exec.Result.IsSuccess.Should().BeTrue();
        cooldown.IsOnCooldown("OpenAI").Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_NonRetryableFailure_DoesNotCountTowardCooldown()
    {
        var cooldown = new CooldownTracker(CooldownOptions.Test); // 2 failures → cooldown
        var sut = new FallbackChainExecutor(
            Substitute.For<ILogger<FallbackChainExecutor>>(),
            RetryPolicy.Test,
            cooldown);

        var primary = MockService("OpenAI", new ChatResult
        {
            ErrorMessage = "401 Unauthorized: bad api key", Model = "gpt-4"
        });

        // Even with multiple calls, non-retryable failures don't accumulate
        // toward cooldown — the request itself is broken, not the provider.
        for (int i = 0; i < 5; i++)
        {
            await sut.ExecuteAsync([primary], new ChatRequest { Model = "gpt-4" });
        }

        cooldown.IsOnCooldown("OpenAI").Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_AllProvidersOnCooldown_ReturnsLastCooldownError()
    {
        var cooldown = new CooldownTracker(CooldownOptions.Test);
        cooldown.RecordFailure("OpenAI");
        cooldown.RecordFailure("OpenAI"); // open
        cooldown.RecordFailure("DeepSeek");
        cooldown.RecordFailure("DeepSeek"); // open

        var sut = new FallbackChainExecutor(
            Substitute.For<ILogger<FallbackChainExecutor>>(),
            RetryPolicy.Test,
            cooldown);

        var p1 = MockService("OpenAI");
        var p2 = MockService("DeepSeek");

        var exec = await sut.ExecuteAsync([p1, p2], new ChatRequest { Model = "gpt-4" });

        exec.Result.IsSuccess.Should().BeFalse();
        exec.Result.ErrorMessage.Should().Contain("cooldown");
        // Neither provider's HTTP call was made
        await p1.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default);
        await p2.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_ProviderThrows_SurfacesSanitizedFailureInsteadOfUnhandledException()
    {
        // Regression: a provider connector that throws on every attempt used to
        // escape the retry policy as a default/null Value, crash the executor with
        // a NullReferenceException, and turn the request into an empty HTTP 500.
        var throwing = MockService("Gemini", throws: new InvalidOperationException("host=internal-db;password=secret"));

        var exec = await _sut.ExecuteAsync([throwing], new ChatRequest { Model = "gpt-4" });

        exec.Result.IsSuccess.Should().BeFalse();
        exec.Result.ErrorMessage.Should().Be("Provider 'Gemini' failed unexpectedly.");
        exec.Result.ErrorMessage.Should().NotContain("secret");
        exec.Result.ErrorMessage.Should().NotContain("internal-db");
        exec.Result.UpstreamStatus.Should().Be(502);
        exec.Attempts.Should().Be(RetryPolicy.Test.MaxAttempts);
    }

    [Fact]
    public async Task ExecuteAsync_PrimaryThrows_FallsThroughToNextProvider()
    {
        var primary = MockService("Gemini", throws: new InvalidOperationException("boom"));
        var fallback = MockService("DeepSeek", new ChatResult { Content = "from fallback", Model = "gpt-4" });

        var exec = await _sut.ExecuteAsync([primary, fallback], new ChatRequest { Model = "gpt-4" });

        exec.Result.IsSuccess.Should().BeTrue();
        exec.Result.Content.Should().Be("from fallback");
        exec.ServedBy.Should().Be(fallback);
        exec.Attempts.Should().Be(RetryPolicy.Test.MaxAttempts + 1);
    }
}
