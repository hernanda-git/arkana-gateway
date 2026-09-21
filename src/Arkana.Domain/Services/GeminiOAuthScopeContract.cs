namespace Arkana.Domain.Services;

/// <summary>
/// The least-privilege OAuth scope contract for native Gemini API access.
/// Keep broker-managed Gemini authentication separate: it does not use this contract.
/// </summary>
public static class GeminiOAuthScopeContract
{
    /// <summary>
    /// Required by the consumer Gemini subscription/native API operation.
    /// </summary>
    public const string PerUserQuota = "https://www.googleapis.com/auth/generative-language.peruserquota";

    /// <summary>Canonical space-separated scope value used in Google authorization requests.</summary>
    public const string Canonical = PerUserQuota;
}
