using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arkana.Application.Features.Chat;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.AI;

namespace Arkana.Infrastructure.Broker;

public sealed record GeminiBrokerRequest(ChatRequest Request, string Slot, string AccountCode, string TraceId, string LogicalProvider);
public sealed record GeminiBrokerResponse(ChatResult Result, AccountAttemptResult Attempt);

/// <summary>Owns a broker response and keeps its HTTP response alive while the stream is consumed.</summary>
public sealed class GeminiBrokerStreamResponse : IAsyncDisposable
{
    private readonly HttpResponseMessage? _response;
    private int _disposed;

    internal GeminiBrokerStreamResponse(HttpResponseMessage? response, Stream? stream, ChatResult result, AccountAttemptResult attempt)
    {
        _response = response;
        Stream = stream;
        Result = result;
        Attempt = attempt;
    }

    public Stream? Stream { get; }
    public ChatResult Result { get; }
    public AccountAttemptResult Attempt { get; }
    public bool IsSuccess => Stream is not null && Result.IsSuccess;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (Stream is not null)
            await Stream.DisposeAsync();
        _response?.Dispose();
    }
}

public interface IGeminiSubscriptionDataPlaneClient
{
    Task<GeminiBrokerResponse> CompleteAsync(GeminiBrokerRequest request, CancellationToken ct = default);
    Task<GeminiBrokerStreamResponse> StreamAsync(GeminiBrokerRequest request, CancellationToken ct = default);
}

/// <summary>Data-plane client. Slot and path are resolved from configuration; callers cannot supply a URL.</summary>
internal sealed class GeminiSubscriptionDataPlaneClient : IGeminiSubscriptionDataPlaneClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _http;
    private readonly CLIProxyManagementOptions _options;
    private readonly Arkana.Infrastructure.AI.Dialects.OpenAiDialectTranslator _translator;

    public GeminiSubscriptionDataPlaneClient(
        IHttpClientFactory http,
        Microsoft.Extensions.Options.IOptions<CLIProxyManagementOptions> options,
        Arkana.Infrastructure.AI.Dialects.OpenAiDialectTranslator translator)
    {
        _http = http;
        _options = options.Value;
        _translator = translator;
    }

    public async Task<GeminiBrokerResponse> CompleteAsync(GeminiBrokerRequest request, CancellationToken ct = default)
    {
        if (!TryResolveSlot(request.Slot, out var baseUri, out var dataPlaneKey))
            return Failure(request, "Broker slot is not allowlisted.", UpstreamFailureClass.ClientError, 400);

        var client = ConfigureClient(baseUri);
        using var message = CreateRequest(request, stream: false, dataPlaneKey);
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var attempt = UpstreamFailureClassifier.Classify(
                    response.StatusCode,
                    response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(CultureInfo.InvariantCulture));
                return new(new ChatResult
                {
                    ErrorMessage = SanitizedStatusMessage(response.StatusCode),
                    Model = request.Request.Model,
                    UpstreamStatus = (int)response.StatusCode,
                    RouteKind = "broker-managed",
                    ResolvedProviderAccountCode = request.AccountCode,
                }, attempt);
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            using var responseJson = JsonDocument.Parse(responseBody);
            if (responseJson.RootElement.TryGetProperty("error", out _))
                return Failure(request, "Gemini subscription upstream returned an error result.", UpstreamFailureClass.ServerError, 502);

            // The broker speaks the OpenAI chat-completion envelope. Parse it with the
            // shared dialect translator: the previous direct ChatResult deserialization
            // silently produced empty content and zero usage because the envelope fields
            // (choices[0].message.content, usage.prompt_tokens/completion_tokens) do not
            // map onto ChatResult's flat shape.
            if (!HasUsableCompletionShape(responseJson.RootElement))
                return Failure(request, "Gemini subscription upstream returned an invalid result.", UpstreamFailureClass.ServerError, 502);

            var result = _translator.FromResponseBody(responseBody, request.Request.Model, TimeSpan.Zero);
            if (result.ErrorMessage is not null)
                return Failure(request, "Gemini subscription upstream returned an invalid result.", UpstreamFailureClass.ServerError, 502);

            return new(result with
            {
                RouteKind = "broker-managed",
                ResolvedProviderAccountCode = request.AccountCode,
            }, AccountAttemptResult.Success());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Failure(request, "Gemini subscription request was cancelled.", UpstreamFailureClass.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return Failure(request, "Gemini subscription upstream request timed out.", UpstreamFailureClass.Timeout);
        }
        catch (Exception ex)
        {
            return Failure(request, "Gemini subscription request failed unexpectedly.", UpstreamFailureClassifier.Classify(ex).FailureClass);
        }
    }

    public async Task<GeminiBrokerStreamResponse> StreamAsync(GeminiBrokerRequest request, CancellationToken ct = default)
    {
        if (!TryResolveSlot(request.Slot, out var baseUri, out var dataPlaneKey))
            return StreamFailure(request, "Broker slot is not allowlisted.", UpstreamFailureClass.ClientError, 400);

        var client = ConfigureClient(baseUri);
        using var message = CreateRequest(request, stream: true, dataPlaneKey);
        HttpResponseMessage? response = null;
        try
        {
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var attempt = UpstreamFailureClassifier.Classify(
                    response.StatusCode,
                    response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(CultureInfo.InvariantCulture));
                return new(response, null, new ChatResult
                {
                    ErrorMessage = SanitizedStatusMessage(response.StatusCode),
                    Model = request.Request.Model,
                    UpstreamStatus = (int)response.StatusCode,
                    RouteKind = "broker-managed",
                    ResolvedProviderAccountCode = request.AccountCode,
                }, attempt);
            }

            var stream = await response.Content.ReadAsStreamAsync(ct);
            return new(response, stream, new ChatResult
            {
                Model = request.Request.Model,
                RouteKind = "broker-managed",
                ResolvedProviderAccountCode = request.AccountCode,
            }, AccountAttemptResult.Success());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            response?.Dispose();
            return StreamFailure(request, "Gemini subscription request was cancelled.", UpstreamFailureClass.Cancelled);
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            return StreamFailure(request, "Gemini subscription upstream request timed out.", UpstreamFailureClass.Timeout);
        }
        catch (Exception ex)
        {
            response?.Dispose();
            return StreamFailure(request, "Gemini subscription request failed unexpectedly.", UpstreamFailureClassifier.Classify(ex).FailureClass);
        }
    }

    private HttpClient ConfigureClient(Uri baseUri)
    {
        var client = _http.CreateClient("gemini-broker-data-plane");
        client.BaseAddress = new Uri(baseUri, baseUri.AbsoluteUri.EndsWith('/') ? string.Empty : "/");
        client.Timeout = _options.Timeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : _options.Timeout;
        return client;
    }

    private static HttpRequestMessage CreateRequest(GeminiBrokerRequest request, bool stream, string dataPlaneKey)
    {
        // The broker speaks the OpenAI chat-completions wire format, where tool
        // turns are named `tool_call_id` / `tool_calls`. Serializing the domain
        // records through the web defaults camelCased those names
        // (`toolCallId` / `toolCalls`); the broker ignores unknown JSON fields,
        // so the upstream never saw the tool exchange and re-issued the same
        // tool call on every follow-up turn. Project the wire shape explicitly.
        var body = new
        {
            model = request.Request.Model,
            messages = request.Request.Messages.Select(m => new
            {
                role = m.Role,
                content = m.Content,
                tool_call_id = m.ToolCallId,
                tool_calls = m.ToolCalls?.Select(tc => new
                {
                    id = tc.Id,
                    type = tc.Type,
                    function = new
                    {
                        name = tc.Function.Name,
                        arguments = tc.Function.Arguments,
                    },
                }),
            }).ToList(),
            tools = request.Request.Tools,
            tool_choice = request.Request.ToolChoice,
            stream,
        };
        var message = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        message.Headers.TryAddWithoutValidation("X-Logical-Provider", request.LogicalProvider);
        message.Headers.TryAddWithoutValidation("X-Provider-Account", request.AccountCode);
        message.Headers.TryAddWithoutValidation("X-Broker-Slot", request.Slot);
        message.Headers.TryAddWithoutValidation("X-Trace-Id", request.TraceId);
        // Data-plane auth: the broker enforces `api-keys` when its config lists any. Sending the
        // slot key keeps a locked-down slot working; a slot with no key at all sends nothing (the
        // broker then trusts the container network, the pre-hardening posture).
        if (!string.IsNullOrWhiteSpace(dataPlaneKey))
            message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {dataPlaneKey}");
        return message;
    }

    private bool TryResolveSlot(string slotName, out Uri baseUri, out string dataPlaneKey)
    {
        baseUri = default!;
        dataPlaneKey = string.Empty;
        if (!_options.Slots.TryGetValue(slotName, out var slot)
            || !Uri.TryCreate(slot.BaseUrl, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https"))
            return false;

        baseUri = parsed;
        dataPlaneKey = slot.EffectiveDataPlaneKey;
        return true;
    }

    private static GeminiBrokerResponse Failure(GeminiBrokerRequest request, string message, UpstreamFailureClass kind, int? status = null)
        => new(new ChatResult
        {
            ErrorMessage = message,
            Model = request.Request.Model,
            UpstreamStatus = status,
            RouteKind = "broker-managed",
            ResolvedProviderAccountCode = request.AccountCode,
        }, AccountAttemptResult.Failure(kind, status));

    private static GeminiBrokerStreamResponse StreamFailure(GeminiBrokerRequest request, string message, UpstreamFailureClass kind, int? status = null)
        => new(null, null, new ChatResult
        {
            ErrorMessage = message,
            Model = request.Request.Model,
            UpstreamStatus = status,
            RouteKind = "broker-managed",
            ResolvedProviderAccountCode = request.AccountCode,
        }, AccountAttemptResult.Failure(kind, status));

    private static string SanitizedStatusMessage(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "Gemini subscription authentication failed.",
        HttpStatusCode.Forbidden => "Gemini subscription authorization failed.",
        (HttpStatusCode)429 => "Gemini subscription rate limit exceeded.",
        _ when (int)status >= 500 => "Gemini subscription upstream service is unavailable.",
        _ => $"Gemini subscription upstream request failed (HTTP {(int)status}).",
    };

    /// <summary>
    /// A usable broker completion is the OpenAI envelope with at least one choice and a
    /// message object. Anything else is rejected as an invalid upstream result instead
    /// of being silently mapped to an empty success.
    /// </summary>
    private static bool HasUsableCompletionShape(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object;
}