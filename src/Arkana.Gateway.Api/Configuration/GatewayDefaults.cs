namespace Arkana.Gateway.Api.Configuration;

/// <summary>
/// Operator-tunable gateway defaults that used to be hardcoded constants.
/// </summary>
/// <remarks>
/// Added 2026-08-06. The fallback/default model was written as the literal
/// <c>"mimo-v2.5"</c> in seven places across two endpoint files. That model
/// belongs to OpenCode, so when the OpenCode workspace quota was exhausted
/// every request that omitted a model — and every request the Responses API
/// remapped — went straight to a dead upstream, even though a healthy
/// independent provider (Ollama) was available. Changing the target required
/// a recompile and redeploy.
///
/// These are read once at startup from the environment so the target can be
/// repointed by editing <c>.env</c> and recreating the container.
/// </remarks>
public static class GatewayDefaults
{
    /// <summary>
    /// Model used when the caller does not specify one, and as the retry
    /// target when an upstream rejects an unknown model name.
    /// Override with <c>GATEWAY_DEFAULT_MODEL</c>.
    /// </summary>
    public static string DefaultModel { get; } =
        FirstNonEmpty(Environment.GetEnvironmentVariable("GATEWAY_DEFAULT_MODEL"), "mimo-v2.5");

    /// <summary>
    /// When true, any model resolving to the OpenCode provider is rewritten to
    /// <see cref="OpenCodeSandboxModel"/> before dispatch.
    ///
    /// This exists because the OpenCode sandbox key historically authorized
    /// only one model, so anything else 403'd. It is a workaround for a
    /// credential limitation, NOT correct routing — it silently overrides the
    /// caller's choice, and once the key is upgraded (or the workspace quota
    /// resets) it does more harm than good. Disable with
    /// <c>OPENCODE_FORCE_SANDBOX_MODEL=0</c>.
    /// </summary>
    public static bool ForceOpenCodeSandboxModel { get; } =
        !string.Equals(
            Environment.GetEnvironmentVariable("OPENCODE_FORCE_SANDBOX_MODEL"),
            "0", StringComparison.Ordinal);

    /// <summary>The single model the OpenCode sandbox key authorizes.</summary>
    public static string OpenCodeSandboxModel { get; } =
        FirstNonEmpty(Environment.GetEnvironmentVariable("OPENCODE_SANDBOX_MODEL"), "mimo-v2.5");

    private static string FirstNonEmpty(string? candidate, string fallback) =>
        string.IsNullOrWhiteSpace(candidate) ? fallback : candidate.Trim();
}
