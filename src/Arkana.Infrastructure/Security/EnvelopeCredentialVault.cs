using System.Security.Cryptography;
using System.Text;
using Arkana.Domain.Services;

namespace Arkana.Infrastructure.Security;

/// <summary>
/// Envelope-encryption credential vault.
///
/// On-disk format (Base64-UTF8):
///   v1:{base64-wrappedDek}:{base64-nonce}:{base64-ciphertext}:{base64-tag}
///
/// Security model:
/// - Each secret gets a fresh 256-bit DEK
/// - The DEK is wrapped (encrypted) with the master key using AES-256-GCM
/// - The plaintext is encrypted with the DEK using AES-256-GCM
/// - The master key is supplied out-of-band (env var, file, or auto-generated on disk with 0600 perms)
///
/// Master key rotation only requires re-wrapping DEKs (not re-encrypting secrets).
/// </summary>
public sealed class EnvelopeCredentialVault : ICredentialVault
{
    private const string VersionTag = "v1";
    private const int DekSizeBytes = 32;        // AES-256
    private const int NonceSizeBytes = 12;      // GCM standard
    private const int TagSizeBytes = 16;        // GCM standard

    private readonly byte[] _masterKey;

    public EnvelopeCredentialVault(byte[] masterKey)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        if (masterKey.Length != DekSizeBytes)
            throw new ArgumentException(
                $"Master key must be exactly {DekSizeBytes} bytes (AES-256). Got {masterKey.Length}.",
                nameof(masterKey));

        // Defensive copy — never retain a reference to the caller's key array
        _masterKey = (byte[])masterKey.Clone();
    }

    public string? Seal(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;

        // 1. Generate a fresh DEK for this secret
        var dek = RandomNumberGenerator.GetBytes(DekSizeBytes);

        // 2. Encrypt the plaintext with the DEK (AES-256-GCM)
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSizeBytes];

        using (var aes = new AesGcm(dek, TagSizeBytes))
        {
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        }

        // 3. Wrap (encrypt) the DEK with the master key (AES-256-GCM)
        var wrapNonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var wrappedDek = new byte[dek.Length];
        var wrapTag = new byte[TagSizeBytes];
        using (var aes = new AesGcm(_masterKey, TagSizeBytes))
        {
            aes.Encrypt(wrapNonce, dek, wrappedDek, wrapTag);
        }

        // 4. Format: v1:wrappedDek:wrapNonce:wrapTag:nonce:ciphertext:tag
        // Including the wrap nonce and wrap tag in the on-disk format is critical
        // — without them the wrapped DEK cannot be unwrapped.
        var sb = new StringBuilder();
        sb.Append(VersionTag).Append(':');
        sb.Append(Convert.ToBase64String(wrappedDek)).Append(':');
        sb.Append(Convert.ToBase64String(wrapNonce)).Append(':');
        sb.Append(Convert.ToBase64String(wrapTag)).Append(':');
        sb.Append(Convert.ToBase64String(nonce)).Append(':');
        sb.Append(Convert.ToBase64String(ciphertext)).Append(':');
        sb.Append(Convert.ToBase64String(tag));

        // 5. Zero out the DEK (we've wrapped it; plaintext is gone)
        CryptographicOperations.ZeroMemory(dek);

        return sb.ToString();
    }

    public string? Open(string? sealedValue)
    {
        if (string.IsNullOrEmpty(sealedValue)) return null;

        var parts = sealedValue.Split(':');
        if (parts.Length != 7 || parts[0] != VersionTag)
            throw new CryptographicException(
                "Sealed value is not in the v1 envelope format. " +
                "The database may contain a legacy plaintext value — re-encrypt via the admin API.");

        byte[] wrappedDek, wrapNonce, wrapTag, nonce, ciphertext, tag;
        try
        {
            wrappedDek = Convert.FromBase64String(parts[1]);
            wrapNonce = Convert.FromBase64String(parts[2]);
            wrapTag   = Convert.FromBase64String(parts[3]);
            nonce     = Convert.FromBase64String(parts[4]);
            ciphertext = Convert.FromBase64String(parts[5]);
            tag       = Convert.FromBase64String(parts[6]);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Sealed value contains invalid base64.", ex);
        }

        // 1. Unwrap the DEK with the master key
        var dek = new byte[DekSizeBytes];
        try
        {
            using var aes = new AesGcm(_masterKey, TagSizeBytes);
            aes.Decrypt(wrapNonce, wrappedDek, wrapTag, dek);
        }
        catch (CryptographicException)
        {
            // Master key mismatch, tampered ciphertext, or wrong format.
            // Do not leak which case — all three must look the same to the caller.
            CryptographicOperations.ZeroMemory(dek);
            throw new CryptographicException(
                "Failed to unwrap DEK. The master key may have changed, or the ciphertext is tampered.");
        }

        // 2. Decrypt the plaintext with the DEK
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(dek, TagSizeBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(plaintext);
            throw new CryptographicException(
                "Failed to decrypt credential. The ciphertext is tampered or corrupted.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }

        var result = Encoding.UTF8.GetString(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        return result;
    }
}
