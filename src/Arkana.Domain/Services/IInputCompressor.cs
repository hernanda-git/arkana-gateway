using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;

namespace Arkana.Domain.Services;

/// <summary>
/// Incoming request transformer that shrinks the prompt before
/// it leaves the gateway (AI-ARKANA-002). Two distinct compressions
/// are useful in different scenarios:
///
///   - **Slimmer** (a model-agnostic heuristic from recent
///     context-management research) drops the middle of long
///     messages when the total exceeds a per-message token
///     threshold. The system prompt and the last user message
///     are always kept verbatim; only the middle is squashed.
///
///   - **Whitespace collapse** is the cheap win: strip
///     runs of blank lines, trim trailing whitespace, and
///     collapse multi-space runs in tool outputs. Often 10-30%
///     of a long context is pure formatting.
///
/// Both are deterministic and zero-LLM. They're meant to run on
/// the request side; output compression (AI-ARKANA-003) is a
/// separate pipeline that runs on the response.
/// </summary>
public interface IInputCompressor
{
    /// <summary>
    /// Return a possibly-shrunken copy of the request. The
    /// returned object is independent of the input — mutating
    /// it must not affect the caller's request.
    /// </summary>
    ChatRequest Compress(ChatRequest request);
}
