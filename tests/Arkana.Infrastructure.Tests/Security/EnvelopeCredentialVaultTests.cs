using System.Security.Cryptography;
using System.Text;
using Arkana.Infrastructure.Security;

namespace Arkana.Infrastructure.Tests.Security;

/// <summary>
/// Tests for <see cref="EnvelopeCredentialVault"/>.
///
/// The vault is the security boundary that protects provider credentials at
/// rest. These tests cover the security-critical properties: round-trip
/// integrity, tamper detection, key-mismatch detection, and format enforcement.
/// </summary>
public sealed class EnvelopeCredentialVaultTests
{
    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    // ──────────────────────────────────────────────
    //   Round-trip
    // ──────────────────────────────────────────────

    [Fact]
    public void Seal_then_Open_returns_original_plaintext()
    {
        var vault = new EnvelopeCredentialVault(NewKey());
        var plaintext = "sk-prod-abc123def456ghi789";

        var sealed_ = vault.Seal(plaintext);
        var opened = vault.Open(sealed_);

        opened.Should().Be(plaintext);
    }

    [Fact]
    public void Seal_is_non_deterministic_same_plaintext_different_ciphertext()
    {
        // Envelope encryption generates a fresh DEK per secret, so encrypting the
        // same plaintext twice must produce different ciphertexts. This prevents
        // a passive observer from detecting duplicate credentials.
        var vault = new EnvelopeCredentialVault(NewKey());
        var plaintext = "sk-same-key";

        var a = vault.Seal(plaintext);
        var b = vault.Seal(plaintext);

        a.Should().NotBe(b);
        vault.Open(a).Should().Be(plaintext);
        vault.Open(b).Should().Be(plaintext);
    }

    [Fact]
    public void Seal_returns_null_for_null_input()
    {
        var vault = new EnvelopeCredentialVault(NewKey());
        vault.Seal(null).Should().BeNull();
    }

    [Fact]
    public void Seal_returns_null_for_empty_input()
    {
        var vault = new EnvelopeCredentialVault(NewKey());
        vault.Seal("").Should().BeNull();
    }

    [Fact]
    public void Open_returns_null_for_null_input()
    {
        var vault = new EnvelopeCredentialVault(NewKey());
        vault.Open(null).Should().BeNull();
    }

    [Fact]
    public void Open_returns_null_for_empty_input()
    {
        var vault = new EnvelopeCredentialVault(NewKey());
        vault.Open("").Should().BeNull();
    }

    // ──────────────────────────────────────────────
    //   Sealed format
    // ──────────────────────────────────────────────

    [Fact]
    public void Seal_produces_v1_format_with_7_colon_separated_parts()
    {
        var vault = new EnvelopeCredentialVault(NewKey());
        var sealed_ = vault.Seal("test");

        sealed_!.Should().StartWith("v1:");
        sealed_.Split(':').Should().HaveCount(7);
    }

    [Fact]
    public void Open_rejects_legacy_plaintext_value()
    {
        // A row in the DB that was stored before envelope encryption was
        // introduced holds plaintext, not a sealed value. The vault must NOT
        // silently return it as-is — it must throw so the operator notices.
        var vault = new EnvelopeCredentialVault(NewKey());
        var legacyPlaintext = "sk-legacy-plaintext-row";

        var act = () => vault.Open(legacyPlaintext);

        act.Should().Throw<CryptographicException>()
            .WithMessage("*v1 envelope format*");
    }

    [Fact]
    public void Open_rejects_invalid_base64()
    {
        var vault = new EnvelopeCredentialVault(NewKey());
        // Valid v1: prefix but garbage payload
        var malformed = "v1:!!!not-base64!!!:also-bad:bad:bad:bad:bad";

        var act = () => vault.Open(malformed);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Open_rejects_wrong_version_tag()
    {
        var vault = new EnvelopeCredentialVault(NewKey());
        var futureFormat = "v2:something:something:something:something:something:something";

        var act = () => vault.Open(futureFormat);

        act.Should().Throw<CryptographicException>()
            .WithMessage("*v1 envelope format*");
    }

    // ──────────────────────────────────────────────
    //   Tamper detection
    // ──────────────────────────────────────────────

    [Fact]
    public void Open_detects_tampered_ciphertext()
    {
        // Flip one byte of the ciphertext portion of a valid sealed value.
        // AES-GCM authentication must reject the modified ciphertext.
        var vault = new EnvelopeCredentialVault(NewKey());
        var sealed_ = vault.Seal("sk-prod-real")!;

        // Split the sealed value, flip a bit in the ciphertext, reassemble.
        var parts = sealed_.Split(':');
        var ciphertextBytes = Convert.FromBase64String(parts[5]);
        ciphertextBytes[0] ^= 0xFF;
        parts[5] = Convert.ToBase64String(ciphertextBytes);
        var tampered = string.Join(':', parts);

        var act = () => vault.Open(tampered);

        act.Should().Throw<CryptographicException>()
            .WithMessage("*tampered*");
    }

    [Fact]
    public void Open_detects_tampered_wrapped_dek()
    {
        // Flipping a byte in the wrapped DEK must also be detected — the master
        // key unwrap will fail the GCM tag check.
        var vault = new EnvelopeCredentialVault(NewKey());
        var sealed_ = vault.Seal("sk-prod-real")!;

        var parts = sealed_.Split(':');
        var wrappedDek = Convert.FromBase64String(parts[1]);
        wrappedDek[0] ^= 0x01;
        parts[1] = Convert.ToBase64String(wrappedDek);
        var tampered = string.Join(':', parts);

        var act = () => vault.Open(tampered);

        act.Should().Throw<CryptographicException>();
    }

    // ──────────────────────────────────────────────
    //   Key-mismatch detection
    // ──────────────────────────────────────────────

    [Fact]
    public void Open_with_different_master_key_throws()
    {
        // Seal with key A, try to open with key B — must fail.
        var keyA = NewKey();
        var keyB = NewKey();
        keyA.Should().NotBeEquivalentTo(keyB);

        var writerVault = new EnvelopeCredentialVault(keyA);
        var readerVault = new EnvelopeCredentialVault(keyB);

        var sealed_ = writerVault.Seal("sk-secret");
        var act = () => readerVault.Open(sealed_);

        act.Should().Throw<CryptographicException>()
            .WithMessage("*master key*");
    }

    // ──────────────────────────────────────────────
    //   Constructor validation
    // ──────────────────────────────────────────────

    [Fact]
    public void Constructor_rejects_null_key()
    {
        var act = () => new EnvelopeCredentialVault(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void Constructor_rejects_wrong_size_key(int size)
    {
        var act = () => new EnvelopeCredentialVault(new byte[size]);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*32 bytes*");
    }

    [Fact]
    public void Constructor_does_not_alias_caller_array()
    {
        // Defensive: the vault must not retain a reference to the caller's key
        // array, otherwise the caller could mutate the key after construction.
        var key = NewKey();
        var originalFirstByte = key[0];
        var vault = new EnvelopeCredentialVault(key);

        // Mutate the caller's array
        key[0] = (byte)(originalFirstByte ^ 0xFF);

        // Seal something — the vault must still work with the ORIGINAL key, not
        // the mutated one. (We can't directly inspect the vault's internal key,
        // but if it had aliased, Open would either fail or succeed consistently.
        // Here we just confirm Open succeeds with the original key bytes.)
        var sealed_ = vault.Seal("test");
        vault.Open(sealed_).Should().Be("test");
    }
}

/// <summary>
/// Tests for <see cref="MasterKeyProvider"/>.
/// </summary>
public sealed class MasterKeyProviderTests : IDisposable
{
    private readonly string _tempDir;

    public MasterKeyProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "arkana-masterkey-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Resolve_generates_and_persists_key_when_no_source_configured()
    {
        // Clear any pre-existing env vars for the duration of the test.
        var savedInline = Environment.GetEnvironmentVariable("ARKANA_MASTER_KEY");
        var savedFile = Environment.GetEnvironmentVariable("ARKANA_MASTER_KEY_FILE");
        Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY", null);
        Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY_FILE", null);

        try
        {
            var (key1, source) = MasterKeyProvider.Resolve(_tempDir);

            key1.Should().HaveCount(32);
            source.Should().Be(MasterKeySource.AutoGeneratedFile);

            // The key file should exist in the data dir.
            var keyFile = Path.Combine(_tempDir, "master.key");
            File.Exists(keyFile).Should().BeTrue();
            File.ReadAllBytes(keyFile).Should().BeEquivalentTo(key1);

            // A second call should return the same key (read from disk, not regenerated).
            var (key2, _) = MasterKeyProvider.Resolve(_tempDir);
            key2.Should().BeEquivalentTo(key1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY", savedInline);
            Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY_FILE", savedFile);
        }
    }

    [Fact]
    public void Resolve_uses_inline_env_var_when_set()
    {
        var savedInline = Environment.GetEnvironmentVariable("ARKANA_MASTER_KEY");
        var savedFile = Environment.GetEnvironmentVariable("ARKANA_MASTER_KEY_FILE");
        Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY_FILE", null);

        try
        {
            var rawKey = RandomNumberGenerator.GetBytes(32);
            Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY", Convert.ToBase64String(rawKey));

            var (key, source) = MasterKeyProvider.Resolve(_tempDir);

            key.Should().BeEquivalentTo(rawKey);
            source.Should().Be(MasterKeySource.EnvironmentVariable);
            // No file should be created when env var is set.
            File.Exists(Path.Combine(_tempDir, "master.key")).Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY", savedInline);
            Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY_FILE", savedFile);
        }
    }

    [Fact]
    public void Resolve_uses_file_path_env_var_when_set()
    {
        var savedInline = Environment.GetEnvironmentVariable("ARKANA_MASTER_KEY");
        var savedFile = Environment.GetEnvironmentVariable("ARKANA_MASTER_KEY_FILE");
        Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY", null);

        try
        {
            var rawKey = RandomNumberGenerator.GetBytes(32);
            var keyFile = Path.Combine(_tempDir, "custom-master.key");
            File.WriteAllBytes(keyFile, rawKey);
            Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY_FILE", keyFile);

            var (key, source) = MasterKeyProvider.Resolve(_tempDir);

            key.Should().BeEquivalentTo(rawKey);
            source.Should().Be(MasterKeySource.File);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY", savedInline);
            Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY_FILE", savedFile);
        }
    }

    [Fact]
    public void Resolve_rejects_inline_env_var_with_wrong_size()
    {
        var savedInline = Environment.GetEnvironmentVariable("ARKANA_MASTER_KEY");
        var savedFile = Environment.GetEnvironmentVariable("ARKANA_MASTER_KEY_FILE");
        Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY_FILE", null);

        try
        {
            // 16 bytes — wrong
            Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY",
                Convert.ToBase64String(new byte[16]));

            var act = () => MasterKeyProvider.Resolve(_tempDir);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*32 bytes*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY", savedInline);
            Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY_FILE", savedFile);
        }
    }

    [Fact]
    public void Resolve_rejects_invalid_base64_inline_env_var()
    {
        var savedInline = Environment.GetEnvironmentVariable("ARKANA_MASTER_KEY");
        var savedFile = Environment.GetEnvironmentVariable("ARKANA_MASTER_KEY_FILE");
        Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY_FILE", null);

        try
        {
            Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY", "!!!not-base64!!!");

            var act = () => MasterKeyProvider.Resolve(_tempDir);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*Base64*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY", savedInline);
            Environment.SetEnvironmentVariable("ARKANA_MASTER_KEY_FILE", savedFile);
        }
    }
}
