using System.Security.Cryptography;
using System.Text;
using Arkana.Domain.Services;

namespace Arkana.Domain.Tests;

/// <summary>
/// In-memory test double for <see cref="ICredentialVault"/>. Uses AES-256-GCM
/// with a deterministic key so tests are reproducible.
///
/// NOTE: This is a TEST DOUBLE only. The production implementation is
/// <c>Arkana.Infrastructure.Security.EnvelopeCredentialVault</c>, which has
/// the same on-disk format (so a row written by the real vault can be read by
/// this test double and vice versa, as long as both use the same key).
/// </summary>
internal sealed class InMemoryCredentialVault : ICredentialVault
{
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const string Prefix = "testv1:";

    private readonly byte[] _key;

    public InMemoryCredentialVault(byte[]? key = null)
    {
        _key = key ?? DeterministicKey;
    }

    private static readonly byte[] DeterministicKey = new byte[]
    {
        0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
        0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10,
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00
    };

    public string? Seal(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[TagSizeBytes];
        using (var aes = new AesGcm(_key, TagSizeBytes))
        {
            aes.Encrypt(nonce, pt, ct, tag);
        }
        return Prefix + Convert.ToBase64String(nonce) + ":" + Convert.ToBase64String(ct) + ":" + Convert.ToBase64String(tag);
    }

    public string? Open(string? sealedValue)
    {
        if (string.IsNullOrEmpty(sealedValue)) return null;
        if (!sealedValue.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Not a testv1 sealed value.");
        var body = sealedValue[Prefix.Length..];
        var parts = body.Split(':');
        if (parts.Length != 3) throw new CryptographicException("Malformed sealed value.");
        var nonce = Convert.FromBase64String(parts[0]);
        var ct = Convert.FromBase64String(parts[1]);
        var tag = Convert.FromBase64String(parts[2]);
        var pt = new byte[ct.Length];
        using (var aes = new AesGcm(_key, TagSizeBytes))
        {
            aes.Decrypt(nonce, ct, tag, pt);
        }
        return Encoding.UTF8.GetString(pt);
    }
}

/// <summary>
/// Test helper to obtain an <see cref="ICredentialVault"/> for domain/entity tests.
/// </summary>
internal static class TestVault
{
    public static ICredentialVault Create() => new InMemoryCredentialVault();
}
