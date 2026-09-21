using System.Text;
using System.Text.RegularExpressions;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// <see cref="IInputCompressor"/> implementation (AI-ARKANA-002).
/// Combines two deterministic transformations:
///
///   1. <b>Whitespace collapse</b> — runs first because it's
///      strictly size-reducing. Strips 3+ blank lines down to 2,
///      and trims trailing whitespace on each line. We do NOT
///      collapse intra-line multi-space runs to preserve code
///      formatting in assistant-generated content.
///
///   2. <b>Slimmer middle-drop</b> — applied per-message. If a
///      single message exceeds the configured
///      <see cref="CompressionOptions.PerMessageCharBudget"/>
///      after whitespace collapse, keep the first
///      <c>ratio * length</c> chars and the last
///      <c>ratio * length</c> chars, replace the middle with
///      a [truncated N chars] marker.
///
/// Both transformations are pure functions of the request —
/// same input always produces the same output. That's
/// important for the response cache (PERF-ARKANA-002) which
/// hashes the request; we want cached entries to be reusable
/// even after the compressor is added.
/// </summary>
public sealed class SlimmerInputCompressor : IInputCompressor
{
    // 3+ consecutive newlines (with optional whitespace between
    // them) collapse to exactly 2 newlines. Compiled once for
    // the lifetime of the process.
    private static readonly Regex TripleNewline = new(
        @"[ \t]*\n[ \t]*(\n[ \t]*)+",
        RegexOptions.Compiled);

    // Per-line trailing-whitespace trim. Multiline so $ matches
    // end-of-line, not end-of-string.
    private static readonly Regex TrailingSpace = new(
        @"[ \t]+\r?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly CompressionOptions _options;
    private readonly ILogger<SlimmerInputCompressor> _logger;

    public SlimmerInputCompressor(
        IOptions<CompressionOptions> options,
        ILogger<SlimmerInputCompressor> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public ChatRequest Compress(ChatRequest request)
    {
        if (!_options.InputEnabled) return request;
        if (request.Messages is null || request.Messages.Count == 0) return request;

        // Always work on a deep copy — never mutate the caller's
        // request. The response cache (PERF-ARKANA-002) hashes the
        // request after this returns, so mutation would either
        // poison the cache key or leak the original request to
        // other consumers.
        var compressed = new List<ChatMessage>(request.Messages.Count);
        long savedChars = 0;

        foreach (var msg in request.Messages)
        {
            var originalLen = msg.Content?.Length ?? 0;
            var collapsed = CollapseWhitespace(msg.Content);
            var slimmed = SlimMiddle(collapsed);
            var finalLen = slimmed.Length;
            var saved = originalLen - finalLen;
            if (saved > 0) savedChars += saved;

            // Preserve every other field verbatim. Tool calls
            // and tool call IDs are the contract with the model —
            // do NOT rewrite them.
            compressed.Add(new ChatMessage
            {
                Role = msg.Role,
                Content = slimmed,
                ToolCallId = msg.ToolCallId,
                ToolCalls = msg.ToolCalls,
            });
        }

        if (savedChars > 0)
        {
            _logger.LogDebug(
                "Slimmer input compression: saved {Saved} chars across {Count} messages",
                savedChars, compressed.Count);
        }

        return new ChatRequest
        {
            Model = request.Model,
            Messages = compressed,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            UserId = request.UserId,
            TenantId = request.TenantId,
            PreferredProviderCode = request.PreferredProviderCode,
            PreferredProviderId = request.PreferredProviderId,
            PreferredProviderAccountId = request.PreferredProviderAccountId,
            PreferredProviderAccountCode = request.PreferredProviderAccountCode,
            AllowProviderFallback = request.AllowProviderFallback,
            AccountRoutingMode = request.AccountRoutingMode,
        };
    }

    private static string CollapseWhitespace(string? content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        var s = content;
        // Per-line trailing-whitespace trim.
        s = TrailingSpace.Replace(s, string.Empty);
        // 3+ newlines (with optional spaces between) → exactly 2.
        s = TripleNewline.Replace(s, "\n\n");
        return s;
    }

    private string SlimMiddle(string content)
    {
        if (content.Length <= _options.PerMessageCharBudget)
            return content;

        var keep = (int)(content.Length * _options.HeadTailKeepRatio);
        if (keep <= 0) return content;

        var dropped = content.Length - 2 * keep;
        var marker = $" [... {dropped} chars truncated by Slimmer input compressor ...] ";
        var headLen = keep;
        var tailStart = content.Length - keep;

        var sb = new StringBuilder(keep * 2 + marker.Length);
        sb.Append(content, 0, headLen);
        sb.Append(marker);
        sb.Append(content, tailStart, keep);
        return sb.ToString();
    }
}
