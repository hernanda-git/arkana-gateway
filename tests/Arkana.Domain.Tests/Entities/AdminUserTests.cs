using Arkana.Domain.Entities;
using Arkana.Domain.Services;

namespace Arkana.Domain.Tests.Entities;

public sealed class AdminUserTests
{
    private static Pbkdf2PasswordHasher Hasher() => new();

    [Fact]
    public void Create_sets_username_lowercased_and_stores_hash()
    {
        var user = AdminUser.Create("Admin", "secret-password", Hasher());

        user.Username.Should().Be("admin");
        user.PasswordHash.Should().StartWith("$pbkdf2-sha256$");
        user.PasswordHash.Should().NotContain("secret-password");
    }

    [Fact]
    public void VerifyPassword_accepts_correct_plaintext()
    {
        var user = AdminUser.Create("admin", "secret-password", Hasher());
        user.VerifyPassword("secret-password", Hasher()).Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_rejects_wrong_plaintext()
    {
        var user = AdminUser.Create("admin", "secret-password", Hasher());
        user.VerifyPassword("wrong-password", Hasher()).Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_rejects_empty_or_null()
    {
        var user = AdminUser.Create("admin", "secret-password", Hasher());
        user.VerifyPassword("", Hasher()).Should().BeFalse();
        user.VerifyPassword(null!, Hasher()).Should().BeFalse();
    }

    [Fact]
    public void SetPassword_replaces_hash_and_clears_must_change()
    {
        var user = AdminUser.Create("admin", "old-password", Hasher());
        user.RequirePasswordChange();
        user.MustChangePassword.Should().BeTrue();

        user.SetPassword("new-password", Hasher());
        user.VerifyPassword("new-password", Hasher()).Should().BeTrue();
        user.VerifyPassword("old-password", Hasher()).Should().BeFalse();
        user.MustChangePassword.Should().BeFalse();
    }

    [Fact]
    public void RecordLogin_sets_last_login_at()
    {
        var user = AdminUser.Create("admin", "secret", Hasher());
        user.LastLoginAt.Should().BeNull();

        user.RecordLogin();
        user.LastLoginAt.Should().NotBeNull();
        user.LastLoginAt!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Two_users_with_same_password_have_different_hashes()
    {
        // Salt is per-user, so two users created with the same plaintext must
        // have different PasswordHash values. Otherwise an attacker who steals
        // one hash could compare it to the entire user table.
        var u1 = AdminUser.Create("alice", "same-password", Hasher());
        var u2 = AdminUser.Create("bob", "same-password", Hasher());
        u1.PasswordHash.Should().NotBe(u2.PasswordHash);
    }

    [Fact]
    public void Create_rejects_empty_username()
    {
        var act = () => AdminUser.Create("", "secret", Hasher());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_null_password()
    {
        var act = () => AdminUser.Create("admin", null!, Hasher());
        act.Should().Throw<ArgumentNullException>();
    }
}
