using System.Text.RegularExpressions;
using Arkana.Domain.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// <see cref="IOutputCompressor"/> implementation (AI-ARKANA-003).
/// Strips filler phrases and collapses excess whitespace from
/// the model's reply so the client receives a tighter payload.
///
/// Why a stateless <see cref="IOutputCompressor.SavedCharacters"/>
/// counter? Useful for tests and for a future OTel metric
/// <c>arkana.compression.saved_chars</c> (not added here —
/// the existing usage metrics cover what we need for now).
/// </summary>
public sealed class TerseOutputCompressor : IOutputCompressor
{
    // Filler openings to strip from the start of a response.
    // Case-insensitive. We anchor on the START of the string —
    // we don't want to delete "Sure, I'll..." in the middle of
    // a paragraph the model is quoting.
    private static readonly Regex FillerPrefix = new(
        @"^\s*(?:Sure[,!.\s]+|Here (?:is|are)(?:\s+the)?[,.\s]+|Let me (?:help you with that|assist you)[,.\s]+|Of course[,!.\s]+|Certainly[,!.\s]+|Absolutely[,!.\s]+|No problem[,!.\s]+|I'd be happy to[,.\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 3+ consecutive newlines → 2.
    private static readonly Regex TripleNewline = new(
        @"(\r?\n){3,}",
        RegexOptions.Compiled);

    // Per-line trailing whitespace. We match horizontal
    // whitespace (space or tab) at the end of each line.
    // Multiline makes $ match end-of-line, not just end-of-string.
    // Verbatim string with \t as the regex tab escape.
    private static readonly Regex TrailingSpace = new(
        @"[ \t]+$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly CompressionOptions _options;
    private readonly ILogger<TerseOutputCompressor> _logger;
    private int _savedCharacters;

    public TerseOutputCompressor(
        IOptions<CompressionOptions> options,
        ILogger<TerseOutputCompressor> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public int SavedCharacters => _savedCharacters;

    public string Compress(string content)
    {
        if (!_options.OutputEnabled) return content;
        if (string.IsNullOrEmpty(content)) return content;

        var original = content;
        var s = FillerPrefix.Replace(content, string.Empty);
        s = TrailingSpace.Replace(s, string.Empty);
        s = TripleNewline.Replace(s, "\n\n");

        var saved = original.Length - s.Length;
        if (saved > 0)
        {
            _savedCharacters += saved;
            _logger.LogDebug(
                "Terse output compression: saved {Saved} chars", saved);
        }
        return s;
    }
}
