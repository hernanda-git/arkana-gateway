using System.Security.Cryptography;
using System.Text;

namespace Arkana.Domain.Services;

/// <summary>
/// Domain service for API key hashing (SHA-256).
/// </summary>
public static class ApiKeyHasher
{
    public static string Hash(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string GenerateApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return $"arkana-{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    /// <summary>
    /// Extracts the first 12 characters of a raw API key for display in the UI.
    /// </summary>
    public static string ExtractPrefix(string rawApiKey)
        => rawApiKey.Length > 12 ? rawApiKey[..12] : rawApiKey;
}
