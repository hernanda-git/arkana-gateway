using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// Connector for connected ChatGPT / Codex subscription accounts.
///
/// Calls the CORRECT Codex endpoint:
///   https://chatgpt.com/backend-api/codex/responses
///
/// with:
///   - Authorization: Bearer &lt;access_token&gt;
///   - chatgpt-account-id: &lt;chatgpt_account_id JWT claim&gt;  (LOWERCASE)
///   - originator: pi
///   - accept: text/event-stream
///   - content-type: application/json
///   - OpenAI-Beta: responses=experimental
///
/// Request body uses the OpenAI Responses API shape (NOT chat/completions):
///   { model, stream, input: ResponseInput, ... }
/// where input = [{ role, content: [{ type: "input_text", text: ... }] }].
///
/// Streaming: reads Responses API SSE (response.output_text.delta etc.) and
/// TRANSLATES back to OpenAI chat/completions SSE so OpenAI-compatible clients
/// (OpenCode, etc.) see the standard choices[].delta.content stream.
/// </summary>
public sealed class ChatGptCodexChatService : IChatCompletionService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly HttpClient _http;
    private readonly ChatGptAccountPool _pool;
    private readonly ILogger<ChatGptCodexChatService> _logger;
    private IAccountUsageSnapshotRepository? _usageRepo;
    private IServiceScopeFactory? _scopeFactory;

    public string ProviderName => "ChatGptCodex";

    /// <summary>Actual OAuth account selected for the current request.</summary>
    public Guid? LastSelectedProviderAccountId { get; private set; }
    public string? LastSelectedAccountCode { get; private set; }

    // The canonical Codex SSE endpoint (confirmed from earendil-works/pi and openai/codex).
    private const string CodexBaseUrl = "https://chatgpt.com/backend-api";
    private static string ResolveCodexUrl() =>
        $"{CodexBaseUrl.TrimEnd('/')}/codex/responses";

    [ActivatorUtilitiesConstructor]
    public ChatGptCodexChatService(
        HttpClient http, ChatGptAccountPool pool,
        ILogger<ChatGptCodexChatService> logger)
        : this(http, pool, logger, null)
    {
    }

    /// <summary>
    /// Full constructor. The usage-snapshot repository is optional (null in
    /// tests / when persistence is unavailable) — usage capture is best-effort
    /// and must never break inference.
    /// </summary>
    public ChatGptCodexChatService(
        HttpClient http, ChatGptAccountPool pool,
        ILogger<ChatGptCodexChatService> logger,
        IAccountUsageSnapshotRepository? usageSnapshotRepository)
    {
        _http = http;
        _pool = pool;
        _logger = logger;
        _usageRepo = usageSnapshotRepository;
    }

    /// <summary>
    /// Translates the model id the client sends into the slug ChatGPT's
    /// /codex/responses backend actually accepts.
    ///
    /// Several OpenAI "marketing" aliases (e.g. <c>gpt-5-codex</c>) are rejected
    /// by the Codex backend with a 400 ("... model is not supported when using
    /// Codex with a ChatGPT account"). The authoritative backend slug is the
    /// dated Codex model (<c>gpt-5.1-codex</c>), corroborated by:
    ///   - openai/codex (Rust CLI) default model,
    ///   - earendil-works/pi client catalog/tests.
    /// Clients may still request the legacy alias; we silently map it upstream.
    /// </summary>
    private static readonly Dictionary<string, string> CodexModelAliasMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Authoritative Codex backend slugs (source of truth: earendil-works/pi
            // packages/ai/scripts/generate-models.ts -> codexModels[]). These are the
            // ONLY ids ChatGPT's /codex/responses accepts today; the old gpt-5-codex /
            // gpt-5.1-codex / gpt-5.2-codex aliases are rejected with a 400.
            ["gpt-5.3-codex-spark"] = "gpt-5.3-codex-spark",
            ["gpt-5.4"] = "gpt-5.4",
            ["gpt-5.4-mini"] = "gpt-5.4-mini",
            ["gpt-5.5"] = "gpt-5.5",
            ["gpt-5.6-luna"] = "gpt-5.6-luna",
            ["gpt-5.6-sol"] = "gpt-5.6-sol",
            ["gpt-5.6-terra"] = "gpt-5.6-terra",
            // Legacy / marketing aliases -> current real Codex slug (gpt-5.4).
            ["gpt-5-codex"] = "gpt-5.4",
            ["gpt-5.1-codex"] = "gpt-5.4",
            ["gpt-5.2-codex"] = "gpt-5.4",
            ["gpt-5"] = "gpt-5.4",
            ["gpt-5.1"] = "gpt-5.4",
        };

    /// <summary>
    /// Resolves the model id to send upstream. Known aliases are rewritten to the
    /// Codex backend slug; anything else (unknown/future slug) is forwarded
    /// verbatim so we never silently break a valid request.
    /// </summary>
    private static string ResolveCodexModel(string? requested) =>
        requested is null ? "gpt-5.4"
        : CodexModelAliasMap.TryGetValue(requested, out var mapped) ? mapped
        : requested;

    // ------------------------------------------------------------------
    //  Non-streaming path — uses the Responses API too (stream:false).
    // ------------------------------------------------------------------

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LastSelectedProviderAccountId = null;
        LastSelectedAccountCode = null;
        _logger.LogInformation("ChatGPT OAuth stage=account_select_start preferred={Preferred}", request.PreferredProviderCode);
        var account = await _pool.SelectAsync(request.TenantId, request.PreferredProviderCode,
            request.AllowProviderFallback, ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
        _logger.LogInformation("ChatGPT OAuth stage=account_select_done account={Account}", account?.Code ?? "none");
        LastSelectedProviderAccountId = account?.ProviderId;
        LastSelectedAccountCode = account?.Code;
        if (account is null)
        {
            return new ChatResult
            {
                ErrorMessage = "No connected ChatGPT account available (all throttled or not yet linked).",
                UpstreamStatus = 503,
                Duration = sw.Elapsed,
            };
        }

        try
        {
            var input = ConvertMessagesToResponsesInput(request.Messages);
            var tools = ConvertToolsToResponsesTools(request.Tools);

            var payload = new
            {
                model = ResolveCodexModel(request.Model),
                store = false,
                stream = false,
                input,
                tools,
                tool_choice = request.ToolChoice ?? "auto",
            };

            using var msg = new HttpRequestMessage(HttpMethod.Post, ResolveCodexUrl())
            {
                Content = JsonContent.Create(payload, options: JsonOpts),
            };
            ApplyCodexHeaders(msg, account);
            using var upstreamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            upstreamCts.CancelAfter(TimeSpan.FromSeconds(30));

            var resp = await _http.SendAsync(msg, upstreamCts.Token);
            CaptureUsageFromHeaders(resp, account);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _pool.MarkThrottled(account.Code, TimeSpan.FromSeconds(30));
                return new ChatResult
                {
                    ErrorMessage = "ChatGPT account rate-limited (429).",
                    UpstreamStatus = 429,
                    Duration = sw.Elapsed,
                };
            }

            if (!resp.IsSuccessStatusCode)
            {
                return new ChatResult
                {
                    ErrorMessage = $"ChatGPT /codex/responses returned {(int)resp.StatusCode}.",
                    UpstreamStatus = (int)resp.StatusCode,
                    Duration = sw.Elapsed,
                };
            }

            // Responses API non-stream response: { output: { content: [{ type: "text", text: "..." }] } }
            var content = ExtractResponsesContent(body);
            if (content is null)
            {
                return new ChatResult
                {
                    ErrorMessage = "Unrecognized ChatGPT /codex/responses response shape.",
                    UpstreamStatus = (int)resp.StatusCode,
                    Duration = sw.Elapsed,
                };
            }

            return new ChatResult
            {
                Content = content,
                Model = request.Model,
                Duration = sw.Elapsed,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChatGPT /codex/responses call failed for account {Code}.", account.Code);
            return new ChatResult
            {
                ErrorMessage = "ChatGPT /codex/responses request failed.",
                Duration = sw.Elapsed,
            };
        }
    }

    // ------------------------------------------------------------------
    //  Streaming path — translate Responses SSE → chat/completions SSE.
    // ------------------------------------------------------------------

    public async Task<(Stream? Stream, int? UpstreamStatus, string? Error)> CompleteStreamingAsync(
        ChatRequest request, CancellationToken ct = default)
    {
        LastSelectedProviderAccountId = null;
        LastSelectedAccountCode = null;
        _logger.LogInformation("ChatGPT OAuth stage=account_select_start preferred={Preferred}", request.PreferredProviderCode);
        var account = await _pool.SelectAsync(request.TenantId, request.PreferredProviderCode,
            request.AllowProviderFallback, ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
        _logger.LogInformation("ChatGPT OAuth stage=account_select_done account={Account}", account?.Code ?? "none");
        LastSelectedProviderAccountId = account?.ProviderId;
        LastSelectedAccountCode = account?.Code;
        if (account is null)
            return (null, 503, "No connected ChatGPT account available (all throttled or not yet linked).");

        try
        {
            var input = ConvertMessagesToResponsesInput(request.Messages);
            var tools = ConvertToolsToResponsesTools(request.Tools);

            var payload = new
            {
                model = ResolveCodexModel(request.Model),
                store = false,
                stream = true,
                input,
                tools,
                tool_choice = request.ToolChoice ?? "auto",
            };

            using var msg = new HttpRequestMessage(HttpMethod.Post, ResolveCodexUrl())
            {
                Content = JsonContent.Create(payload, options: JsonOpts),
            };
            ApplyCodexHeaders(msg, account);
            msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var upstreamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            upstreamCts.CancelAfter(TimeSpan.FromSeconds(30));

            var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, upstreamCts.Token);
            CaptureUsageFromHeaders(resp, account);

            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _pool.MarkThrottled(account.Code, TimeSpan.FromSeconds(30));
                return (null, 429, "ChatGPT account rate-limited (429).");
            }

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return (null, (int)resp.StatusCode,
                    $"ChatGPT /codex/responses returned {(int)resp.StatusCode}.");
            }

            // Wrap the upstream stream in a translating stream.
            var upstream = await resp.Content.ReadAsStreamAsync(ct);
            var translator = new TranslatingStream(upstream, _logger, ct)
            {
                SourceAccount = account,
                UsageSink = CaptureUsageSnapshot,
            };
            return (translator.Output, (int)resp.StatusCode, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChatGPT /codex/responses streaming call failed for account {Code}.", account.Code);
            return (null, null, "ChatGPT /codex/responses streaming request failed.");
        }
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    /// <summary>Builds the exact headers the Codex SSE endpoint requires.</summary>
    private static void ApplyCodexHeaders(HttpRequestMessage msg, ChatGptAccount account)
    {
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        // LOWERCASE — confirmed from pi reference (buildBaseCodexHeaders / buildSSEHeaders).
        msg.Headers.Add("chatgpt-account-id", account.AccountId ?? account.Code);
        msg.Headers.Add("originator", "pi");
        msg.Headers.Add("OpenAI-Beta", "responses=experimental");
        msg.Headers.UserAgent.ParseAdd("pi/1.0");
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        // Set content-type on the content, not the headers collection.
        if (msg.Content != null)
            msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }

    // ------------------------------------------------------------------
    //  Upstream usage-window capture (best-effort, never breaks inference)
    // ------------------------------------------------------------------

    /// <summary>One parsed upstream rate-limit window observation.</summary>
    internal sealed record CodexUsageWindow(double? UsedPercent, int? WindowMinutes, long? ResetAtEpochSeconds);

    /// <summary>Parsed upstream Codex rate-limit report for one response.</summary>
    internal sealed record CodexRateLimitReport(
        CodexUsageWindow? Primary, CodexUsageWindow? Secondary, string? PlanType)
    {
        public bool HasAny => Primary is not null || Secondary is not null;
    }

    /// <summary>Header names of the x-codex-* rate-limit family (per official codex-rs client).</summary>
    private const string PrimaryUsedPercentHeader = "x-codex-primary-used-percent";
    private const string PrimaryWindowMinutesHeader = "x-codex-primary-window-minutes";
    private const string PrimaryResetAtHeader = "x-codex-primary-reset-at";
    private const string SecondaryUsedPercentHeader = "x-codex-secondary-used-percent";
    private const string SecondaryWindowMinutesHeader = "x-codex-secondary-window-minutes";
    private const string SecondaryResetAtHeader = "x-codex-secondary-reset-at";
    private const string PlanTypeHeader = "x-codex-plan-type";

    /// <summary>
    /// Parses the upstream's <c>x-codex-*-used-percent</c> header family into a
    /// <see cref="CodexRateLimitReport"/>. Mirrors parse_rate_limit_for_limit()
    /// in openai/codex codex-api/src/rate_limits.rs. Returns null when no
    /// rate-limit headers are present.
    /// </summary>
    internal static CodexRateLimitReport? ParseRateLimitHeaders(HttpResponseHeaders headers) =>
        ParseRateLimitReport(
            ReadHeaderDouble(headers, PrimaryUsedPercentHeader),
            ReadHeaderInt(headers, PrimaryWindowMinutesHeader),
            ReadHeaderLong(headers, PrimaryResetAtHeader),
            ReadHeaderDouble(headers, SecondaryUsedPercentHeader),
            ReadHeaderInt(headers, SecondaryWindowMinutesHeader),
            ReadHeaderLong(headers, SecondaryResetAtHeader),
            ReadHeaderString(headers, PlanTypeHeader));

    /// <summary>
    /// Parses a <c>codex.rate_limits</c> SSE event payload (JSON). Mirrors
    /// parse_rate_limit_event() in the official client. Returns null when the
    /// payload is not that event.
    /// </summary>
    internal static CodexRateLimitReport? ParseRateLimitEventPayload(string jsonData)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonData);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String
                || typeEl.GetString() != "codex.rate_limits")
                return null;

            root.TryGetProperty("plan_type", out var planEl);
            string? planType = planEl.ValueKind == JsonValueKind.String ? planEl.GetString() : null;

            root.TryGetProperty("rate_limits", out var rlEl);

            (double?, int?, long?) primary = (null, null, null);
            if (rlEl.ValueKind == JsonValueKind.Object && rlEl.TryGetProperty("primary", out var pEl))
                primary = ParseEventWindow(pEl);
            (double?, int?, long?) secondary = (null, null, null);
            if (rlEl.ValueKind == JsonValueKind.Object && rlEl.TryGetProperty("secondary", out var sEl))
                secondary = ParseEventWindow(sEl);

            return ParseRateLimitReport(
                primary.Item1, primary.Item2, primary.Item3,
                secondary.Item1, secondary.Item2, secondary.Item3,
                planType);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (double?, int?, long?) ParseEventWindow(JsonElement windowEl)
    {
        if (windowEl.ValueKind != JsonValueKind.Object) return (null, null, null);
        double? used = windowEl.TryGetProperty("used_percent", out var uEl) && uEl.ValueKind == JsonValueKind.Number
            && uEl.TryGetDouble(out var ud) ? ud : null;
        int? minutes = windowEl.TryGetProperty("window_minutes", out var wEl) && wEl.ValueKind == JsonValueKind.Number
            && wEl.TryGetInt32(out var wi) ? wi : null;
        long? reset = windowEl.TryGetProperty("reset_at", out var rEl) && rEl.ValueKind == JsonValueKind.Number
            && rEl.TryGetInt64(out var rl) ? rl : null;
        return (used, minutes, reset);
    }

    private static CodexRateLimitReport? ParseRateLimitReport(
        double? pUsed, int? pMinutes, long? pReset,
        double? sUsed, int? sMinutes, long? sReset, string? planType)
    {
        var primary = pUsed.HasValue ? new CodexUsageWindow(pUsed, pMinutes, pReset) : null;
        var secondary = sUsed.HasValue ? new CodexUsageWindow(sUsed, sMinutes, sReset) : null;
        if (primary is null && secondary is null) return null;
        return new CodexRateLimitReport(primary, secondary, planType);
    }

    private static double? ReadHeaderDouble(HttpResponseHeaders headers, string name) =>
        TryReadNumber(headers, name, out double d) ? d : null;

    private static int? ReadHeaderInt(HttpResponseHeaders headers, string name) =>
        TryReadNumber(headers, name, out int i) ? i : null;

    private static long? ReadHeaderLong(HttpResponseHeaders headers, string name) =>
        TryReadNumber(headers, name, out long l) ? l : null;

    private static bool TryReadNumber<T>(HttpResponseHeaders headers, string name, out T value)
        where T : IParsable<T>
    {
        var raw = ReadHeaderString(headers, name);
        if (raw is not null && T.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }
        value = default!;
        return false;
    }

    private static string? ReadHeaderString(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values)) return null;
        var raw = values.FirstOrDefault();
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    /// <summary>
    /// Persists an upstream rate-limit observation as the latest snapshot per
    /// (account × window). Best-effort: failures are logged and swallowed —
    /// usage capture must never break inference.
    /// </summary>
    private void CaptureUsageSnapshot(ChatGptAccount account, CodexRateLimitReport report)
    {
        if (_usageRepo is null || !report.HasAny) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // The request scope is disposed when inference completes. Resolve
                // a fresh repository scope for this detached best-effort write.
                using var scope = _scopeFactory?.CreateScope();
                var usageRepo = scope?.ServiceProvider.GetService<IAccountUsageSnapshotRepository>() ?? _usageRepo;
                if (usageRepo is null) return;

                if (report.Primary is { UsedPercent: not null } p)
                {
                    await usageRepo.UpsertAsync(new AccountUsageSnapshot(
                        account.ProviderId, account.Code, UsageWindowKind.Primary,
                        p.UsedPercent!.Value, p.WindowMinutes, ToUtc(p.ResetAtEpochSeconds),
                        report.PlanType));
                }
                if (report.Secondary is { UsedPercent: not null } s)
                {
                    await usageRepo.UpsertAsync(new AccountUsageSnapshot(
                        account.ProviderId, account.Code, UsageWindowKind.Secondary,
                        s.UsedPercent!.Value, s.WindowMinutes, ToUtc(s.ResetAtEpochSeconds),
                        report.PlanType));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist ChatGPT usage snapshot for {Code}.", account.Code);
            }
        });
    }

    private static DateTimeOffset? ToUtc(long? epochSeconds) =>
        epochSeconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(epochSeconds.Value) : null;

    /// <summary>Header capture hook — safe on any response status (429 included).</summary>
    private void CaptureUsageFromHeaders(HttpResponseMessage resp, ChatGptAccount account)
    {
        try
        {
            var report = ParseRateLimitHeaders(resp.Headers);
            if (report is not null)
            {
                _logger.LogInformation(
                    "ChatGPT usage windows for {Code}: primary={P}% secondary={S}%. Snapshot persisted.",
                    account.Code,
                    report.Primary?.UsedPercent?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "?",
                    report.Secondary?.UsedPercent?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "?");
                CaptureUsageSnapshot(account, report);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse ChatGPT usage headers for {Code}.", account.Code);
        }
    }

    /// <summary>
    /// Converts canonical ToolDefinition[] to the tool shape ChatGPT's
    /// /codex/responses backend accepts. Unlike the OpenAI Responses API
    /// (which nests under `function`), the Codex endpoint expects the
    /// Chat-Completions-style FLAT shape:
    ///   [{ type: "function", name, description, parameters }].
    /// Sending the nested `function` wrapper yields
    /// "Missing required parameter: 'tools[0].name'".
    /// </summary>
    private static JsonArray? ConvertToolsToResponsesTools(IReadOnlyList<ToolDefinition>? tools)
    {
        if (tools is null || tools.Count == 0) return null;
        var arr = new JsonArray();
        foreach (var t in tools)
        {
            var fn = new JsonObject
            {
                ["type"] = "function",
                ["name"] = t.Function.Name,
            };
            if (!string.IsNullOrEmpty(t.Function.Description))
                fn["description"] = t.Function.Description;
            if (t.Function.Parameters is { } p && p.ValueKind == JsonValueKind.Object)
                fn["parameters"] = JsonNode.Parse(p.GetRawText());
            else
                fn["parameters"] = new JsonObject();
            arr.Add(fn);
        }
        return arr;
    }

    /// <summary>
    /// Converts OpenAI chat messages into the Responses API input shape.
    /// Returns a JsonArray of [{ role, content: [{ type: "input_text", text: ... }] }].
    /// </summary>
    internal static JsonArray? ConvertMessagesToResponsesInput(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0) return null;

        // BUG FIX #2 (2026-08-24, synthetic-call-id): every
        // function_call_output MUST pair with a function_call item carrying the
        // SAME call_id, and that id must be one the upstream actually issued —
        // ChatGPT's backend rejects both orphan outputs ("No tool call found
        // for function call output with call_id …") and call_ids it did not
        // issue (synthetic/gateway-invented). Resolution per tool result:
        //   - explicit id matching an unpaired emitted function_call → consume
        //     it and emit a properly paired output;
        //   - no explicit id but an unpaired function_call exists → reuse that
        //     real id (strictly better than degrading to text);
        //   - anything else (unknown id, nothing open) → FLATTEN the result
        //     into ordinary input text, never inventing a call_id.
        // Any function_call left unpaired at the end (its output was flattened
        // away or never arrived) is dropped, so the request never contains a
        // dangling call either.
        var outArray = new JsonArray();
        // Ids of function_call items emitted but not yet answered by their
        // function_call_output. Anything left here when the loop ends is a
        // dangling call (its result was flattened away or never arrived).
        var openCalls = new List<string>();
        // BUG FIX #3 (2026-08-26, tooluse-id): ChatGPT /codex/responses
        // rejects ANY function_call id that does not begin with "fc"
        // ("Invalid 'input[N].id': 'tooluse_…'. Expected an ID that begins with
        // 'fc'"). Some clients (Continue.dev) replay history with their own
        // Anthropic-style ids (tooluse_*). Both the function_call and its
        // output carry the SAME client id, so strict pairing succeeds — and we
        // would forward a rejected id verbatim. Fix: deterministically remap
        // every non-fc id to fc_<n> and apply the SAME mapping to both sides
        // of each pair, preserving pairing semantics.
        var idRewrite = new Dictionary<string, string>(StringComparer.Ordinal);
        string RewriteId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return id ?? "";
            if (id.StartsWith("fc", StringComparison.Ordinal)) return id;
            if (!idRewrite.TryGetValue(id, out var mapped))
            {
                mapped = $"fc_{idRewrite.Count + 1}";
                idRewrite[id] = mapped;
            }
            return mapped;
        }

        foreach (var m in messages)
        {
            var obj = new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = new JsonArray()
            };
            var contentArr = (JsonArray)obj["content"]!;
            var text = m.Content;

            // ChatGPT /codex/responses uses the Responses API input shape:
            //   - user/system messages: { role, content: [{ type: "input_text", text: ... }] }
            //   - assistant messages:    { role, content: [{ type: "output_text", text: ... }] }
            //   - tool messages:        { type: "function_call_output", call_id, output }
            // The Responses API does NOT accept { role: "tool", content: [...] }.
            // Convert tool-role messages to function_call_output items so they inspect
            // correctly (fixes "Invalid value: 'tool'" 400 from zero CLI exec_command).

            if (string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                string? pairedId = null;
                if (!string.IsNullOrWhiteSpace(m.ToolCallId))
                {
                    // Match against BOTH the raw client id and its remapped
                    // form — openCalls stores rewritten ids.
                    if (openCalls.Remove(RewriteId(m.ToolCallId)))
                        pairedId = RewriteId(m.ToolCallId);
                    else if (idRewrite.TryGetValue(m.ToolCallId, out var alreadyMapped) &&
                             openCalls.Remove(alreadyMapped))
                        pairedId = alreadyMapped;
                }
                else if (string.IsNullOrWhiteSpace(m.ToolCallId) && openCalls.Count > 0)
                {
                    // Missing id: reuse the most recent unpaired real id.
                    pairedId = openCalls[^1];
                    openCalls.RemoveAt(openCalls.Count - 1);
                }

                if (pairedId is not null)
                {
                    outArray.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = pairedId,
                        ["output"] = text,
                    });
                }
                else
                {
                    // Unknown/missing id with no reusable call: flatten to plain
                    // text rather than send a call_id the backend never issued
                    // (flatten-stateless-continuation strategy).
                    outArray.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "input_text",
                                ["text"] = $"[Tool result]\n{text}",
                            }
                        },
                    });
                }
                continue;
            }
            if (!string.IsNullOrWhiteSpace(text))
            {
                // OpenAI Responses API: user/system content is "input_text";
                // assistant content must be "output_text" (ChatGPT rejects
                // "input_text" for assistant turns with the error
                // "Invalid value: 'input_text'. Supported values are:
                // 'output_text' and 'refusal'.").
                var contentType = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? "output_text"
                    : "input_text";
                contentArr.Add(new JsonObject
                {
                    ["type"] = contentType,
                    ["text"] = text,
                });
            }
            if (contentArr.Count > 0 || m.ToolCalls is not { Count: > 0 })
                outArray.Add(obj);

            if (string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && m.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in m.ToolCalls)
                {
                    var callId = RewriteId(
                        string.IsNullOrWhiteSpace(toolCall.Id) ? "call_0" : toolCall.Id);
                    outArray.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["id"] = callId,
                        ["call_id"] = callId,
                        ["name"] = toolCall.Function.Name,
                        ["arguments"] = toolCall.Function.Arguments,
                    });
                    openCalls.Add(callId);
                }
            }
        }

        // Drop any function_call whose output never arrived (or was flattened):
        // the upstream rejects a dangling function_call just like an orphan
        // output. The model simply re-issues the call if it still needs it.
        if (openCalls.Count > 0)
        {
            for (int i = outArray.Count - 1; i >= 0; i--)
            {
                if (outArray[i] is JsonObject node &&
                    node["type"]?.GetValue<string>() == "function_call" &&
                    openCalls.Contains(node["call_id"]?.GetValue<string>() ?? ""))
                {
                    outArray.RemoveAt(i);
                }
            }
        }
        return outArray;
    }

    /// <summary>
    /// Extracts text from a non-streaming Responses API response.
    /// Response shape: { output: { content: [{ type: "text", text: "..." }] } }
    /// Also handles { output_text: "..." } fallback and legacy { choices } shape.
    /// </summary>
    private static string? ExtractResponsesContent(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Primary: output.content[].text
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object)
            {
                if (output.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var item in content.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var type) &&
                            type.ValueKind == JsonValueKind.String &&
                            type.GetString() == "text" &&
                            item.TryGetProperty("text", out var text) &&
                            text.ValueKind == JsonValueKind.String)
                        {
                            sb.Append(text.GetString());
                        }
                    }
                    var result = sb.ToString();
                    if (result.Length > 0) return result;
                }
            }

            // Fallback: direct output_text field
            if (root.TryGetProperty("output_text", out var ot) && ot.ValueKind == JsonValueKind.String)
                return ot.GetString();

            // Fallback: choices[].message.content (legacy /codex compat)
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in choices.EnumerateArray())
                {
                    if (c.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.String)
                        return content.GetString();
                    if (c.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        return text.GetString();
                }
            }
        }
        catch { /* best-effort */ }
        return null;
    }

    // ------------------------------------------------------------------
    //  SSE translator: Responses API events → chat/completions SSE lines.
    // ------------------------------------------------------------------

    /// <summary>
    /// Reads a ChatGPT /codex/responses SSE stream ( Responses API events ),
    /// extracts text deltas from response.output_text.delta events, and writes
    /// OpenAI chat/completions SSE lines into a Pipe that the caller reads from.
    ///
    /// Chat/completions SSE format emitted:
    ///   data: {"choices":[{"delta":{"content":"..."},"index":0,"finish_reason":null}]}
    ///   ...
    ///   data: {"choices":[{"delta":{"content":"..."},"index":0,"finish_reason":"stop"}]}
    ///   data: [DONE]
    ///
    /// Wraps the upstream ChatGPT /codex SSE stream and translates it into
    /// OpenAI chat/completions SSE, written INCREMENTALLY to a Pipe so the
    /// client receives tokens as they arrive (no whole-response buffering) and
    /// the stream closes cleanly on EOF.
    /// </summary>
    /// <summary>
    /// Test hook: exposes the private SSE translator so regression tests can pin
    /// its translation contract against synthetic upstream streams. Returns the
    /// translated output stream and a probe for the translator's Failed flag.
    /// </summary>
    internal static (Stream Output, Func<bool> FailedProbe) CreateTranslatingStreamForTests(
        Stream upstream, ILogger logger, CancellationToken ct)
    {
        var translator = new TranslatingStream(upstream, logger, ct);
        return (translator.Output, () => translator.Failed);
    }

    /// <summary>Test/diagnostic hook: parses one SSE data payload for a codex.rate_limits event.</summary>
    internal static CodexRateLimitReport? TryParseRateLimitEventForTests(string data) =>
        ParseRateLimitEventPayload(data);

    /// <summary>
    /// Attaches the usage-snapshot repository post-construction (the typed
    /// HttpClient factory has no parameterless slot for optional services).
    /// Fluent; safe to call with null to disable capture.
    /// </summary>
    internal ChatGptCodexChatService WithUsageRepository(
        IAccountUsageSnapshotRepository? repository,
        IServiceScopeFactory? scopeFactory = null)
    {
        _usageRepo = repository;
        _scopeFactory = scopeFactory;
        return this;
    }

    private sealed class TranslatingStream : Stream
    {
        private readonly Pipe _pipe = new();
        private readonly Task _pumpTask;
        private volatile bool _failed;

        /// <summary>
        /// Optional observer for upstream codex.rate_limits SSE events:
        /// (account, parsed report). Null when usage capture is unavailable.
        /// </summary>
        internal Action<ChatGptAccount, CodexRateLimitReport>? UsageSink { get; set; }

        /// <summary>The account this upstream stream belongs to (for the usage sink).</summary>
        internal ChatGptAccount? SourceAccount { get; set; }

        /// <summary>
        /// True when the upstream pump ended abnormally (network error or client
        /// abort) rather than a clean end-of-stream. The endpoint layer checks
        /// this to surface a real failure instead of silently truncating.
        /// </summary>
        public bool Failed => _failed;

        public TranslatingStream(Stream upstream, ILogger logger, CancellationToken externalCt)
        {
            _pumpTask = Task.Run(async () =>
            {
                try
                {
                    await PumpAndTranslateAsync(upstream, _pipe.Writer, logger, this, externalCt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Real pump failure (network reset, malformed upstream, ...).
                    // Mark the output as failed so the consumer sees a hard error
                    // instead of a silently truncated stream.
                    logger.LogError(ex, "ChatGptCodex SSE translation pump failed.");
                    _failed = true;
                }
                catch (OperationCanceledException)
                {
                    // Client disconnected mid-stream: the endpoint is gone, the
                    // read loop was cancelled. This is normal teardown noise —
                    // log at Information, do NOT alarm.
                    logger.LogInformation(
                        "ChatGptCodex SSE translation pump cancelled (client disconnect or request aborted).");
                    _failed = true;
                }
                finally
                {
                    try { await _pipe.Writer.CompleteAsync(); } catch { }
                }
            }, externalCt);
        }

        /// <summary>The translated chat/completions stream for the consumer.</summary>
        public Stream Output => _pipe.Reader.AsStream();

        private static async Task PumpAndTranslateAsync(Stream upstream, PipeWriter writer, ILogger logger,
            TranslatingStream owner, CancellationToken ct)
        {
            var rawBuf = new byte[8192];
            var textAccum = new StringBuilder();
            var utf8Decoder = Encoding.UTF8.GetDecoder();
            // Tracks whether we have an open function call so the final
            // finish_reason is "tool_calls" (not "stop") only when appropriate,
            // and whether the upstream already sent its own completion frame
            // (so we don't emit a duplicate [DONE]).
            var functionCallActive = false;
            var responseCompleted = false;
            // Set when the premature-EOF handler emits its own synthetic finish,
            // so the tail-of-loop fallback does not write a second one.
            var syntheticFinishEmitted = false;
            string? activeToolCallId = null;
            string? activeToolName = null;

            while (!ct.IsCancellationRequested)
            {
                int read = await upstream.ReadAsync(rawBuf, ct);
                if (read == 0)
                {
                    // Upstream closed the connection. If the Responses API did
                    // not deliver its own terminal event first, the stream was
                    // cut mid-flight — emit a synthetic finish so the client's
                    // tool loop can recover, and LOG it. Previously this path
                    // was completely silent, which made truncation invisible.
                    if (!responseCompleted)
                    {
                        logger.LogWarning(
                            "ChatGptCodex upstream SSE ended prematurely before a terminal event "
                            + "(truncated generation). Emitting synthetic finish_reason={Reason}.",
                            functionCallActive ? "tool_calls" : "stop");
                        WriteChatCompletionsDelta(writer, "", functionCallActive ? "tool_calls" : "stop");
                        WriteDone(writer);
                        await writer.FlushAsync(ct);
                        owner._failed = true;
                        syntheticFinishEmitted = true;
                    }
                    break;
                }

                var charBuf = new char[read * 2];
                int charCount = utf8Decoder.GetChars(rawBuf.AsSpan(0, read), charBuf.AsSpan(0, charBuf.Length), true);
                textAccum.Append(charBuf, 0, charCount);

                string remaining;
                while (TryExtractFrame(textAccum, out var frame, out remaining))
                {
                    textAccum.Clear();
                    textAccum.Append(remaining);
                    TranslateFrame(frame, writer, logger, ref functionCallActive, ref responseCompleted,
                        ref activeToolCallId, ref activeToolName, owner, ct);
                    await writer.FlushAsync(ct);
                }
            }

            if (textAccum.Length > 0)
            {
                TranslateFrame(textAccum.ToString(), writer, logger, ref functionCallActive, ref responseCompleted,
                    ref activeToolCallId, ref activeToolName, owner, ct);
                await writer.FlushAsync(ct);
            }

            // Final finish frame + [DONE], but only if the upstream didn't
            // already terminate the stream itself (response.completed/done/error)
            // and the premature-EOF handler above didn't already emit one.
            if (!responseCompleted && !syntheticFinishEmitted)
            {
                WriteChatCompletionsDelta(writer, "", functionCallActive ? "tool_calls" : "stop");
                WriteDone(writer);
                await writer.FlushAsync(ct);
            }
        }

        private static bool TryExtractFrame(StringBuilder sb, out string frame, out string remaining)
        {
            int idx = sb.ToString().IndexOf("\n\n", StringComparison.Ordinal);
            if (idx < 0)
            {
                frame = "";
                remaining = sb.ToString();
                return false;
            }
            frame = sb.ToString(0, idx);
            remaining = sb.ToString(idx + 2, sb.Length - idx - 2);
            return true;
        }

        private static void TranslateFrame(string chunk, PipeWriter writer, ILogger logger,
            ref bool functionCallActive, ref bool responseCompleted,
            ref string? activeToolCallId, ref string? activeToolName,
            TranslatingStream owner, CancellationToken ct)
        {
            var lines = chunk.Split('\n');
            string? eventType = null;
            var dataLines = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("event:", StringComparison.Ordinal))
                    eventType = trimmed.Length > 6 ? trimmed[6..].Trim() : null;
                else if (trimmed.StartsWith("data:", StringComparison.Ordinal))
                {
                    var payload = trimmed.Length > 5 ? trimmed[5..].TrimEnd() : "";
                    if (payload.Length > 0)
                        dataLines.Add(payload);
                }
            }

            if (dataLines.Count == 0) return;

            foreach (var data in dataLines)
            {
                if (data == "[DONE]") return;

                string? type = eventType;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                        type ??= t.GetString();
                    if (type == null) continue;

                    // Upstream usage-window report — observe, persist, and pass
                    // through untouched to the default branch (no translation).
                    if (type == "codex.rate_limits")
                    {
                        var report = ParseRateLimitEventPayload(data);
                        var sink = owner.UsageSink;
                        var source = owner.SourceAccount;
                        if (report is not null && sink is not null && source is not null)
                        {
                            try { sink(source, report); }
                            catch { /* never break the stream on usage capture */ }
                        }
                        continue;
                    }

                    switch (type)
                    {
                        case "response.output_text.delta":
                            var delta = ExtractDeltaText(root);
                            if (delta != null && delta.Length > 0)
                                WriteChatCompletionsDelta(writer, delta, null);
                            break;

                        case "response.output_item.added":
                        case "response.output_item.delta":
                            HandleFunctionCallItem(root, writer, ref functionCallActive,
                                ref activeToolCallId, ref activeToolName);
                            break;

                        case "response.function_call_arguments.delta":
                            if (root.TryGetProperty("delta", out var argDelta) &&
                                argDelta.ValueKind == JsonValueKind.String)
                            {
                                if (activeToolCallId is null || activeToolName is null)
                                {
                                    activeToolCallId ??= root.TryGetProperty("item_id", out var itemId)
                                        && itemId.ValueKind == JsonValueKind.String
                                        ? itemId.GetString()
                                        : "call_0";
                                    activeToolName = root.TryGetProperty("name", out var n)
                                        && n.ValueKind == JsonValueKind.String
                                        ? n.GetString()
                                        : "unknown_tool";
                                    functionCallActive = true;
                                    WriteToolCallOpen(writer, activeToolCallId!, activeToolName);
                                }
                                WriteToolCallDelta(writer, argDelta.GetString() ?? "", activeToolCallId);
                            }
                            break;

                        case "response.completed":
                        case "response.done":
                            // Extract token usage if the upstream reported it.
                            // Responses API nests it under response.usage (or sometimes top-level).
                            JsonElement? usageEl = null;
                            if (root.TryGetProperty("response", out var respObj) && respObj.ValueKind == JsonValueKind.Object
                                && respObj.TryGetProperty("usage", out var ru) && ru.ValueKind == JsonValueKind.Object)
                                usageEl = ru;
                            else if (root.TryGetProperty("usage", out var tu) && tu.ValueKind == JsonValueKind.Object)
                                usageEl = tu;
                            if (usageEl is not null)
                                WriteUsage(writer, usageEl.Value);
                            WriteChatCompletionsDelta(writer, "", functionCallActive ? "tool_calls" : "stop");
                            WriteDone(writer);
                            responseCompleted = true;
                            return;

                        case "response.incomplete":
                            // The upstream deliberately stopped early (most common:
                            // max_output_tokens reached while the model was still
                            // reasoning/producing). This is NOT a clean stop —
                            // surface finish_reason="length" so OpenAI-compatible
                            // clients know the output was cut and can continue or
                            // retry, instead of believing the answer just ended.
                            logger.LogWarning(
                                "ChatGptCodex upstream reported response.incomplete (output truncated, e.g. max_output_tokens). "
                                + "Mapping finish_reason to 'length'.");
                            JsonElement? incUsage = null;
                            if (root.TryGetProperty("response", out var iResp) && iResp.ValueKind == JsonValueKind.Object
                                && iResp.TryGetProperty("usage", out var iu) && iu.ValueKind == JsonValueKind.Object)
                                incUsage = iu;
                            else if (root.TryGetProperty("usage", out var itu) && itu.ValueKind == JsonValueKind.Object)
                                incUsage = itu;
                            if (incUsage is not null)
                                WriteUsage(writer, incUsage.Value);
                            WriteChatCompletionsDelta(writer, "", functionCallActive ? "tool_calls" : "length");
                            WriteDone(writer);
                            responseCompleted = true;
                            owner._failed = true;
                            return;

                        case "response.failed":
                            // The generation itself failed upstream (content
                            // policy, server error event, etc.). Emit an explicit
                            // error payload so clients see a failure rather than
                            // an empty-but-successful turn.
                            string? failReason = null;
                            if (root.TryGetProperty("response", out var fResp) && fResp.ValueKind == JsonValueKind.Object
                                && fResp.TryGetProperty("error", out var ferr) && ferr.ValueKind == JsonValueKind.Object
                                && ferr.TryGetProperty("message", out var fmsg) && fmsg.ValueKind == JsonValueKind.String)
                                failReason = fmsg.GetString();
                            logger.LogError(
                                "ChatGptCodex upstream reported response.failed: {Reason}", failReason ?? "(no detail)");
                            var errEnvelope = new
                            {
                                error = new { message = failReason ?? "Upstream ChatGPT Codex stream failed.", type = "upstream_error" },
                            };
                            var errJson = JsonSerializer.Serialize(errEnvelope, JsonOpts);
                            var errBytes = Encoding.UTF8.GetBytes($"data: {errJson}\n\n");
                            var errMem = writer.GetMemory(errBytes.Length);
                            errBytes.CopyTo(errMem.Span);
                            writer.Advance(errBytes.Length);
                            WriteDone(writer);
                            responseCompleted = true;
                            owner._failed = true;
                            return;

                        case "error":
                            logger.LogWarning("ChatGptCodex upstream sent SSE error event; terminating translated stream.");
                            WriteChatCompletionsDelta(writer, "", "stop");
                            WriteDone(writer);
                            responseCompleted = true;
                            owner._failed = true;
                            return;

                        default:
                            break;
                    }
                }
                catch (JsonException) { }
            }
        }

        private static string? ExtractDeltaText(JsonElement root)
        {
            if (!root.TryGetProperty("delta", out var deltaEl)) return null;
            if (deltaEl.ValueKind == JsonValueKind.String) return deltaEl.GetString();
            if (deltaEl.ValueKind == JsonValueKind.Object &&
                deltaEl.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
                return content.GetString();
            return null;
        }

        private static void WriteChatCompletionsDelta(PipeWriter w, string delta, string? finishReason)
        {
            var envelope = new
            {
                choices = new[]
                {
                    new
                    {
                        delta = delta.Length > 0
                            ? new { content = delta }
                            : (object?)null,
                        index = 0,
                        finish_reason = finishReason,
                    }
                }
            };
            var json = JsonSerializer.Serialize(envelope, JsonOpts);
            var line = Encoding.UTF8.GetBytes($"data: {json}\n\n");
            var mem = w.GetMemory(line.Length);
            line.CopyTo(mem.Span);
            w.Advance(line.Length);
        }

        private static void WriteDone(PipeWriter w)
        {
            var done = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
            var dmem = w.GetMemory(done.Length);
            done.CopyTo(dmem.Span);
            w.Advance(done.Length);
        }

        private static void WriteUsage(PipeWriter w, JsonElement usage)
        {
            int input = 0, output = 0;
            if (usage.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number) input = it.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number) output = ot.GetInt32();

            var usageEnvelope = new
            {
                usage = new
                {
                    prompt_tokens = input,
                    completion_tokens = output,
                    total_tokens = input + output,
                },
                choices = new object[0],
            };
            var json = JsonSerializer.Serialize(usageEnvelope, JsonOpts);
            var line = Encoding.UTF8.GetBytes($"data: {json}\n\n");
            var mem = w.GetMemory(line.Length);
            line.CopyTo(mem.Span);
            w.Advance(line.Length);
        }

        private static void WriteToolCallOpen(PipeWriter w, string id, string? name)
        {
            var toolCalls = new[]
            {
                new
                {
                    id,
                    type = "function",
                    function = new
                    {
                        name = name ?? "",
                        arguments = "",
                    },
                },
            };
            var envelope = new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new { tool_calls = toolCalls },
                        index = 0,
                        finish_reason = (string?)null,
                    },
                },
            };
            var json = JsonSerializer.Serialize(envelope, JsonOpts);
            var line = Encoding.UTF8.GetBytes($"data: {json}\n\n");
            var mem = w.GetMemory(line.Length);
            line.CopyTo(mem.Span);
            w.Advance(line.Length);
        }

        private static void WriteToolCallDelta(PipeWriter w, string argumentsFragment, string? id = null)
        {
            var toolCalls = new[]
            {
                new
                {
                    id,
                    index = 0,
                    function = new
                    {
                        arguments = argumentsFragment,
                    },
                },
            };
            var envelope = new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new { tool_calls = toolCalls },
                        index = 0,
                        finish_reason = (string?)null,
                    },
                },
            };
            var json = JsonSerializer.Serialize(envelope, JsonOpts);
            var line = Encoding.UTF8.GetBytes($"data: {json}\n\n");
            var mem = w.GetMemory(line.Length);
            line.CopyTo(mem.Span);
            w.Advance(line.Length);
        }

        private static void HandleFunctionCallItem(JsonElement root, PipeWriter writer,
            ref bool functionCallActive, ref string? activeToolCallId, ref string? activeToolName)
        {
            if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                return;
            if (!item.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                return;
            if (typeEl.GetString() != "function_call")
                return;

            var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()!
                : activeToolCallId ?? "call_0";

            var isNewToolCall = !string.Equals(activeToolCallId, id, StringComparison.Ordinal);
            functionCallActive = true;
            activeToolCallId = id;
            activeToolName = item.TryGetProperty("name", out var initialName)
                && initialName.ValueKind == JsonValueKind.String
                ? initialName.GetString()
                : activeToolName;

            if (isNewToolCall && item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                WriteToolCallOpen(writer, id, nameEl.GetString()!);

            if (item.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
            {
                var args = argsEl.GetString()!;
                if (args.Length > 0)
                    WriteToolCallDelta(writer, args, activeToolCallId);
            }
        }

        // -- Stream implementation: delegate to the Pipe reader ---------------

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Output.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            => Output.ReadAsync(buffer, cancellationToken);

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
