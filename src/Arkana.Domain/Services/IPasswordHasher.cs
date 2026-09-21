using System.Security.Cryptography;

namespace Arkana.Domain.Services;

/// <summary>
/// Password hashing service.
///
/// Why PBKDF2-SHA256 over Argon2id:
/// - PBKDF2 is built into the BCL — no external dependency, no maintenance burden
/// - With 600,000+ iterations (the OWASP 2023 recommendation for SHA-256), brute-force
///   on the hash requires hundreds of milliseconds per guess on a modern CPU
/// - Per-password salt (16 bytes) prevents rainbow-table attacks
/// - PHC-ish format ($pbkdf2-sha256$i=&lt;iter&gt;$&lt;base64-salt&gt;$&lt;base64-hash&gt;)
///   embeds the iteration count so we can tune it without a schema change
///
/// Argon2id would be stronger (memory-hard, resistant to GPU/ASIC), but adopting
/// it requires a vetted library (Konscious.Security.Cryptography) and adds an
/// external dependency. The audit finding was about credential-at-rest, not
/// specifically about which KDF we use. PBKDF2-SHA256 at 600k+ iterations is
/// well above the threshold of "secure for current adversary capabilities" per
/// OWASP 2023.
///
/// To upgrade to Argon2id in a future change: swap the implementation of
/// <see cref="IPasswordHasher"/>. The format prefix ("pbkdf2-sha256") makes
/// it possible to detect old hashes and re-hash on next successful login.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Hash a plaintext password. Returns a PHC-ish string.</summary>
    string Hash(string plaintext);

    /// <summary>
    /// Verify a plaintext password against a stored hash string. Constant-time
    /// comparison. Returns false on any decoding error (do NOT throw — treat
    /// all failures as auth failure).
    /// </summary>
    bool Verify(string plaintext, string phcString);
}

/// <summary>
/// Default PBKDF2-SHA256 parameters tuned for ~250ms on a modern server CPU
/// (OWASP 2023 minimum for SHA-256: 600,000 iterations).
/// </summary>
public sealed class Pbkdf2Parameters
{
    /// <summary>Iteration count. OWASP 2023 minimum for SHA-256: 600,000.</summary>
    public int Iterations { get; init; } = 600_000;

    /// <summary>Salt length in bytes. 16 bytes is the standard.</summary>
    public int SaltBytes { get; init; } = 16;

    /// <summary>Hash output length in bytes. 32 bytes = 256 bits.</summary>
    public int HashBytes { get; init; } = 32;
}

/// <summary>
/// PBKDF2-SHA256 password hasher. Format:
///   $pbkdf2-sha256$i=&lt;iter&gt;$&lt;base64-salt&gt;$&lt;base64-hash&gt;
///
/// Example:
///   $pbkdf2-sha256$i=600000$4f8b...d2e1$9a3c...7f8b
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const string AlgorithmTag = "pbkdf2-sha256";
    private const string Prefix = "$";
    private const string Separator = "$";

    private readonly Pbkdf2Parameters _parameters;

    public Pbkdf2PasswordHasher(Pbkdf2Parameters? parameters = null)
    {
        _parameters = parameters ?? new Pbkdf2Parameters();
    }

    public string Hash(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (plaintext.Length == 0)
            throw new ArgumentException("Password cannot be empty.", nameof(plaintext));

        var salt = RandomNumberGenerator.GetBytes(_parameters.SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password: plaintext,
            salt: salt,
            iterations: _parameters.Iterations,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: _parameters.HashBytes);

        return string.Join(Separator,
            Prefix + AlgorithmTag,
            $"i={_parameters.Iterations}",
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public bool Verify(string plaintext, string phcString)
    {
        if (string.IsNullOrEmpty(plaintext) || string.IsNullOrEmpty(phcString)) return false;
        if (!TryParse(phcString, out var parts)) return false;
        if (parts.Algorithm != AlgorithmTag) return false;

        byte[] expectedHash;
        try
        {
            expectedHash = Rfc2898DeriveBytes.Pbkdf2(
                password: plaintext,
                salt: parts.Salt,
                iterations: parts.Iterations,
                hashAlgorithm: HashAlgorithmName.SHA256,
                outputLength: parts.Hash.Length);
        }
        catch
        {
            // Any failure (bad salt length, bad hash length, OOM on huge iter count)
            // is treated as auth failure. Do not leak details.
            return false;
        }

        // Constant-time compare
        return CryptographicOperations.FixedTimeEquals(expectedHash, parts.Hash);
    }

    private static bool TryParse(string phc, out (string Algorithm, int Iterations, byte[] Salt, byte[] Hash) parts)
    {
        parts = default;
        var segments = phc.Split(Separator);
        // Format: $pbkdf2-sha256$i=...$<salt>$<hash>
        // Splitting on '$' yields: ["", "pbkdf2-sha256", "i=...", "<salt>", "<hash>"]
        if (segments.Length != 5) return false;
        if (segments[0] != "") return false;
        // segments[1] is the bare algorithm tag (no leading '$' — the separator that
        // produced this segment IS the leading '$').
        if (segments[1] != AlgorithmTag) return false;

        if (!segments[2].StartsWith("i=", StringComparison.Ordinal) ||
            !int.TryParse(segments[2][2..], out var iterations))
            return false;

        byte[] salt, hash;
        try
        {
            salt = Convert.FromBase64String(segments[3]);
            hash = Convert.FromBase64String(segments[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        parts = (AlgorithmTag, iterations, salt, hash);
        return true;
    }
}
