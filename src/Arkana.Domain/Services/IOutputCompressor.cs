namespace Arkana.Domain.Services;

/// <summary>
/// Response post-processor (AI-ARKANA-003). Compresses the
/// assistant's reply so the client receives a leaner payload
/// and any subsequent re-prompt carries less overhead.
///
/// Two transformations apply:
///   - **Whitespace collapse** — strip runs of blank lines,
///     trim trailing whitespace, collapse 3+ newlines to 2.
///   - **Terse phrasing** — strip filler phrases ("Sure, ",
///     "Here is the ", "Let me help you with that, ").
///
/// Why post-process at the gateway, not just at the model? The
/// model prompt itself can ask for terse output, but most
/// off-the-shelf model responses still pad. Doing it at the
/// gateway means the savings apply uniformly regardless of
/// which model served the request, and they're free for cached
/// responses.
/// </summary>
public interface IOutputCompressor
{
    /// <summary>Return a possibly-shrunken copy of the response text.</summary>
    string Compress(string content);

    /// <summary>How many characters were saved (sum of saved lengths).</summary>
    int SavedCharacters { get; }
}
