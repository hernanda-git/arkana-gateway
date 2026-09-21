using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Microsoft.Extensions.Logging;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// Default <see cref="IProviderConnectorFactory"/> implementation.
/// Resolves an <see cref="IChatCompletionService"/> for a given
/// <see cref="AiProvider"/> by looking up its <c>Code</c> against the
/// registered connectors.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 (Phase 3, task 14c). Today's behavior: the four OpenAI-compat
/// connectors (OpenAI, OpenCode, DeepSeek, CLIProxyAPI) all share the
/// <see cref="Dialects.OpenAiDialectTranslator"/>, so the factory resolves
/// any of the four codes to the appropriate connector instance.
///
/// ChatGPT / Codex accounts (codes <c>chatgpt</c> and <c>chatgpt-accN</c>) are
/// all served by the single <see cref="ChatGptCodexChatService"/> connector;
/// the account pool inside it does the per-account selection, so the factory
/// just maps any <c>chatgpt*</c> code to it (created at runtime — no
/// recompilation needed when a new account is added via the dashboard).
///
/// Adding a new dialect (Anthropic, Gemini, Groq, etc.) is:
/// <list type="number">
///   <item>Write the new <c>IDialectTranslator</c> implementation.</item>
///   <item>Write the new connector.</item>
///   <item>Add the <c>HttpClient</c> registration in <c>DependencyInjection</c>.</item>
///   <item>Inject the new connector into this factory and add its
///         <c>AiProvider.Code</c> to <see cref="RegisterDialect"/>.</item>
/// </list>
/// No changes to the router, the fallback chain, or the handler.
/// </remarks>
internal sealed class ProviderConnectorFactory : IProviderConnectorFactory
{
    /// <summary>
    /// Keyed lookup: lowercase provider code -> connector instance.
    /// Built at ctor time from the connectors DI hands us; explicit
    /// aliases can be added via <see cref="RegisterDialect"/>.
    /// </summary>
    private readonly Dictionary<string, IChatCompletionService> _byCode;

    private readonly ILogger<ProviderConnectorFactory> _logger;

    /// <summary>
    /// The single ChatGPT / Codex connector that serves every
    /// <c>chatgpt-accN</c> account (account selection is internal to it).
    /// Resolved lazily so the factory still works when the connector is
    /// not registered.
    /// </summary>
    private readonly ChatGptCodexChatService? _chatGptConnector;

    /// <summary>
    /// The single CLIProxyAPI connector that serves every <c>gemini-accN</c>
    /// account. Each account is its own <see cref="AiProvider"/> row with its
    /// own BaseUrl (a distinct cliproxy container); the connector resolves the
    /// target account from <see cref="ChatRequest.PreferredProviderCode"/>.
    /// Resolved lazily so the factory still works when the connector is
    /// not registered.
    /// </summary>
    private readonly GeminiSubscriptionChatService? _geminiSubscriptionConnector;
    private readonly IProviderTargetPlanner? _targetPlanner;
    private readonly ITenantProvider? _tenantProvider;

    public ProviderConnectorFactory(
        IEnumerable<IChatCompletionService> connectors,
        ILogger<ProviderConnectorFactory> logger,
        ChatGptCodexChatService? chatGptConnector = null,
        CLIProxyAPIChatService? cliProxyConnector = null,
        GeminiSubscriptionChatService? geminiSubscriptionConnector = null,
        IProviderTargetPlanner? targetPlanner = null,
        ITenantProvider? tenantProvider = null)
    {
        _byCode = new Dictionary<string, IChatCompletionService>(StringComparer.OrdinalIgnoreCase);
        _logger = logger;
        // The typed HttpClient registration guarantees the ChatGPT connector is
        // present in IEnumerable<IChatCompletionService>, but it does not always
        // make the concrete type directly resolvable as an optional constructor
        // parameter. Resolve it from the collection so chatgpt-accN providers are
        // never silently dropped from fallback chains.
        _chatGptConnector = chatGptConnector
            ?? connectors.OfType<ChatGptCodexChatService>().FirstOrDefault();
        _geminiSubscriptionConnector = geminiSubscriptionConnector
            ?? connectors.OfType<GeminiSubscriptionChatService>().FirstOrDefault();
        _targetPlanner = targetPlanner;
        _tenantProvider = tenantProvider;

        // Index every connector by its lowercase ProviderName. The seed
        // data in GatewayDbContext uses lowercase codes ("opencode",
        // "openai", "deepseek", "cliproxyapi") and each connector's
        // ProviderName is the same identifier in PascalCase ("OpenCode",
        // "OpenAI", "DeepSeek", "CLIProxyAPI"). We bridge the two with
        // a case-insensitive compare so lookups work either way.
        foreach (var connector in connectors)
        {
            if (string.IsNullOrEmpty(connector.ProviderName))
            {
                _logger.LogWarning(
                    "Connector {Type} has no ProviderName; it will not be reachable by code",
                    connector.GetType().Name);
                continue;
            }

            var key = connector.ProviderName.ToLowerInvariant();
            if (_byCode.ContainsKey(key))
            {
                _logger.LogWarning(
                    "Duplicate connector registration for provider code {Code}; keeping the first",
                    key);
                continue;
            }

            _byCode[key] = connector;
        }

        _logger.LogInformation(
            "ProviderConnectorFactory initialized with {Count} providers: {Codes}",
            _byCode.Count, string.Join(", ", _byCode.Keys));
    }

    /// <summary>
    /// Register an additional provider code that maps to an existing
    /// connector. Used when one connector implementation should serve
    /// multiple catalog codes (e.g. one Anthropic connector serving
    /// "anthropic", "claude-3-opus", "claude-3-sonnet" as separate
    /// provider rows with different pricing).
    /// </summary>
    public void RegisterDialect(string providerCode, IChatCompletionService connector)
    {
        if (string.IsNullOrEmpty(providerCode) || connector is null)
        {
            throw new ArgumentException("providerCode and connector are required");
        }

        _byCode[providerCode.ToLowerInvariant()] = connector;
    }

    public IReadOnlyList<string> RegisteredDialectCodes =>
        _byCode.Keys.ToList();

    public async Task<IChatCompletionService?> ResolveAsync(AiProvider provider, CancellationToken ct = default)
    {
        if (provider is null)
        {
            return null;
        }

        // ChatGPT / Codex: every chatgpt* account code routes to the single
        // multi-account connector (account selection happens inside it).
        if (_chatGptConnector is not null &&
            provider.Code.StartsWith("chatgpt", StringComparison.OrdinalIgnoreCase))
        {
            return _chatGptConnector;
        }

        if (provider.Code.Equals("gemini", StringComparison.OrdinalIgnoreCase))
        {
            return _byCode.TryGetValue("gemini", out var nativeGemini) ? nativeGemini : null;
        }

        // Account aliases are input syntax only. Resolve their ownership from
        // the tenant-scoped ProviderAccount row before selecting a connector.
        if (provider.Code.StartsWith("gemini-acc", StringComparison.OrdinalIgnoreCase)
            || provider.Code.Equals("gemini-subscription", StringComparison.OrdinalIgnoreCase))
        {
            if (_targetPlanner is null || _tenantProvider?.TenantId is not { } tenantId)
                return null;

            var target = await _targetPlanner.ResolveAsync(tenantId, provider, string.Empty, ct: ct);
            if (!target.IsResolved)
            {
                _logger.LogWarning("Gemini provider target rejected for code {Code}: {Reason}",
                    provider.Code, target.RejectionReason);
                return null;
            }

            return target.RouteKind switch
            {
                ProviderRouteKind.NativeGemini when _byCode.TryGetValue("gemini", out var native) => native,
                ProviderRouteKind.BrokerManagedGemini => _geminiSubscriptionConnector,
                _ => null,
            };
        }

        if (_byCode.TryGetValue(provider.Code, out var connector))
        {
            return connector;
        }

        _logger.LogDebug(
            "No connector registered for provider code {Code} (provider '{Name}', id={Id})",
            provider.Code, provider.Name, provider.Id);

        return null;
    }
}
