namespace Arkana.Domain.Services;

/// <summary>
/// Encrypts and decrypts provider API credentials at rest.
///
/// Implementations MUST:
/// - Never return the plaintext outside the Open/OpenAsync call (avoid retaining decrypted values)
/// - Authenticate ciphertext (AEAD — AES-GCM, ChaCha20-Poly1305, etc.)
/// - Derive or accept a master key from outside the database (env var, file, KMS)
/// - Be safe to call concurrently from multiple threads
/// </summary>
public interface ICredentialVault
{
    /// <summary>
    /// Encrypts plaintext and returns the on-disk form. Returns null if plaintext is null or empty.
    /// </summary>
    string? Seal(string? plaintext);

    /// <summary>
    /// Decrypts the on-disk form and returns the plaintext. Returns null if the input is null or empty.
    /// Throws <see cref="System.Security.Cryptography.CryptographicException"/> if the ciphertext
    /// is tampered with or cannot be authenticated.
    /// </summary>
    string? Open(string? sealedValue);
}
