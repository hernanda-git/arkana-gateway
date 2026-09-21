using Arkana.Domain.Services;

namespace Arkana.Domain.Tests.Security;

public sealed class Pbkdf2PasswordHasherTests
{
    private static Pbkdf2PasswordHasher NewHasher() => new();

    [Fact]
    public void Hash_produces_phc_format()
    {
        var hash = NewHasher().Hash("correct horse battery staple");

        hash.Should().StartWith("$pbkdf2-sha256$i=");
        hash.Split('$').Should().HaveCount(5);
    }

    [Fact]
    public void Verify_accepts_correct_password()
    {
        var hasher = NewHasher();
        var hash = hasher.Hash("hunter2");
        hasher.Verify("hunter2", hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_rejects_wrong_password()
    {
        var hasher = NewHasher();
        var hash = hasher.Hash("hunter2");
        hasher.Verify("hunter3", hash).Should().BeFalse();
    }

    [Fact]
    public void Hash_is_non_deterministic_same_password_different_salts()
    {
        var hasher = NewHasher();
        var a = hasher.Hash("same-password");
        var b = hasher.Hash("same-password");
        a.Should().NotBe(b);
        // Both still verify
        hasher.Verify("same-password", a).Should().BeTrue();
        hasher.Verify("same-password", b).Should().BeTrue();
    }

    [Fact]
    public void Verify_returns_false_for_garbage_input()
    {
        var hasher = NewHasher();
        hasher.Verify("any", "garbage").Should().BeFalse();
        hasher.Verify("any", "").Should().BeFalse();
        hasher.Verify("", "any").Should().BeFalse();
        hasher.Verify("", "").Should().BeFalse();
    }

    [Fact]
    public void Verify_returns_false_for_null()
    {
        var hasher = NewHasher();
        hasher.Verify(null!, "any").Should().BeFalse();
        hasher.Verify("any", null!).Should().BeFalse();
    }

    [Fact]
    public void Verify_rejects_wrong_algorithm_prefix()
    {
        // A hash that has a different algorithm tag (e.g. future Argon2id)
        // should not be accepted by the PBKDF2 hasher.
        var fakeArgon2 = "$argon2id$v=19$m=65536,t=3,p=1$c2FsdA$aGFzaA";
        NewHasher().Verify("any", fakeArgon2).Should().BeFalse();
    }

    [Fact]
    public void Verify_rejects_malformed_segments()
    {
        var hasher = NewHasher();
        // Wrong number of segments
        hasher.Verify("any", "$pbkdf2-sha256").Should().BeFalse();
        hasher.Verify("any", "$pbkdf2-sha256$i=100000").Should().BeFalse();
        // Invalid base64
        hasher.Verify("any", "$pbkdf2-sha256$i=100000$!!!not-base64!!!$alsogarbage").Should().BeFalse();
        // Non-numeric iteration count
        hasher.Verify("any", "$pbkdf2-sha256$i=abc$YWFh$YmJi").Should().BeFalse();
    }

    [Fact]
    public void Hash_rejects_null_or_empty_password()
    {
        var hasher = NewHasher();
        var act1 = () => hasher.Hash(null!);
        act1.Should().Throw<ArgumentNullException>();
        var act2 = () => hasher.Hash("");
        act2.Should().Throw<ArgumentException>();
    }
}
