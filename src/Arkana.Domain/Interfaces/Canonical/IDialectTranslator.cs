using System.Text.Json;

namespace Arkana.Domain.Interfaces.Canonical;

/// <summary>
/// Translates between the provider-neutral <see cref="CanonicalChatRequest"/>
/// and a provider's wire format. The translator owns dialect concerns only:
/// JSON shape, message-role mapping, tool-call representation, response parsing.
/// HTTP transport, auth, and retry live in the connector that calls the translator.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 (Phase 3, task 14b-1). One implementation per provider dialect:
/// <list type="bullet">
///   <item><c>OpenAiDialectTranslator</c> — OpenAI, OpenCode, DeepSeek, CLIProxyAPI</item>
///   <item><c>AnthropicDialectTranslator</c> — Anthropic Messages API (lands in #15)</item>
///   <item><c>GeminiDialectTranslator</c> — Google Gemini generateContent (lands in #15)</item>
/// </list>
/// The connector decides which translator to use based on the configured
/// <c>AiProvider.Code</c> at request time (see <c>IProviderConnectorFactory</c>,
/// task 14c).
/// </remarks>
public interface IDialectTranslator
{
    /// <summary>
    /// Short identifier matching <c>AiProvider.Code</c>. Used by the
    /// factory to map a provider row to the right translator.
    /// </summary>
    string DialectCode { get; }

    /// <summary>
    /// Project the canonical request onto this dialect's wire body.
    /// Returns a JSON-serializable <see cref="Dictionary{TKey, TValue}"/>
    /// so the connector can hand it straight to <c>JsonContent.Create</c>
    /// or <c>JsonSerializer.Serialize</c> without further shaping.
    /// </summary>
    /// <param name="request">Canonical request to translate. Never null.</param>
    /// <returns>Wire body as a snake_case-keyed dictionary. Caller owns serialization.</returns>
    Dictionary<string, object?> ToRequestBody(CanonicalChatRequest request);

    /// <summary>
    /// Parse an upstream response into the gateway's <see cref="ChatResult"/>
    /// shape. The connector passes the raw response stream (UTF-8 JSON);
    /// the translator returns either a successful result or sets
    /// <see cref="ChatResult.ErrorMessage"/>.
    /// </summary>
    /// <param name="responseJson">Raw JSON body from the upstream.</param>
    /// <param name="requestedModel">Model the caller asked for, used as a fallback
    /// when the upstream omits the model field.</param>
    /// <param name="duration">Elapsed time for the upstream call, set by the connector.</param>
    ChatResult FromResponseBody(string responseJson, string requestedModel, TimeSpan duration);
}
