namespace Arkana.Domain.Services;

/// <summary>
/// Configuration for the input/output compressors
/// (AI-ARKANA-002, AI-ARKANA-003).
/// </summary>
public sealed class CompressionOptions
{
    /// <summary>appsettings section name. Always use the constant.</summary>
    public const string Section = "Compression";

    /// <summary>Master enable for <see cref="IInputCompressor"/>.</summary>
    // CA1805 suppressed: the explicit "= false" is intentional in
    // the options-pattern to make the default visible at the call
    // site. "Options default is false" reads better than "default
    // is the default for bool, which is false".
#pragma warning disable CA1805
    public bool InputEnabled { get; set; } = false;
#pragma warning restore CA1805

    /// <summary>Master enable for <see cref="IOutputCompressor"/>.</summary>
#pragma warning disable CA1805
    public bool OutputEnabled { get; set; } = false;
#pragma warning restore CA1805

    /// <summary>
    /// Estimated tokens per character for the Slimmer-style
    /// budget. OpenAI's published ratio is ~0.25 tokens/char
    /// for English; 4 chars/token is the conservative number
    /// we use here.
    /// </summary>
    public double CharsPerToken { get; set; } = 4.0;

    /// <summary>
    /// Per-message character budget. If a single message
    /// (after whitespace collapse) exceeds this, the Slimmer
    /// transform drops the middle. 16k chars ~= 4k tokens,
    /// which is roughly the threshold where a single message
    /// starts to dominate the context.
    /// </summary>
    public int PerMessageCharBudget { get; set; } = 16_000;

    /// <summary>
    /// Head/tail keep ratio when the Slimmer transform runs.
    /// 0.2 = keep the first 20% and the last 20% of the
    /// message, replace the middle 60% with a placeholder.
    /// </summary>
    public double HeadTailKeepRatio { get; set; } = 0.2;
}
