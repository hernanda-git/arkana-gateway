using Arkana.Domain.Services;

namespace Arkana.Domain.Entities;

/// <summary>
/// A dashboard user. The single-user bootstrap creates a default admin on first run
/// (configurable via ARKANA_ADMIN_PASSWORD env var, otherwise a strong random
/// password is generated and printed to the startup log).
///
/// SECURITY: <see cref="PasswordHash"/> holds the PHC-format Argon2id/PBKDF2 hash,
/// not the plaintext. Plaintext is only ever set via <see cref="SetPassword"/>
/// which immediately hashes and discards the plaintext.
/// </summary>
public sealed class AdminUser
{
    public Guid Id { get; private set; }
    public string Username { get; private set; } = string.Empty;

    /// <summary>PHC-format password hash (e.g. <c>$pbkdf2-sha256$i=600000$...$...</c>).</summary>
    public string PasswordHash { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }

    /// <summary>True when the user must change their password on next login.</summary>
    public bool MustChangePassword { get; private set; }

    private AdminUser() { }

    public static AdminUser Create(string username, string plaintextPassword, IPasswordHasher hasher)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentNullException.ThrowIfNull(plaintextPassword);

        return new AdminUser
        {
            Id = Guid.NewGuid(),
            Username = username.ToLowerInvariant(),
            PasswordHash = hasher.Hash(plaintextPassword),
            CreatedAt = DateTimeOffset.UtcNow,
            MustChangePassword = false
        };
    }

    /// <summary>
    /// Replaces the password. The plaintext is hashed immediately; the original
    /// string is not retained by this method (caller should overwrite their copy).
    /// </summary>
    public void SetPassword(string plaintextPassword, IPasswordHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(plaintextPassword);
        PasswordHash = hasher.Hash(plaintextPassword);
        MustChangePassword = false;
    }

    public bool VerifyPassword(string plaintextPassword, IPasswordHasher hasher)
    {
        if (string.IsNullOrEmpty(plaintextPassword)) return false;
        return hasher.Verify(plaintextPassword, PasswordHash);
    }

    public void RecordLogin()
    {
        LastLoginAt = DateTimeOffset.UtcNow;
    }

    public void RequirePasswordChange() => MustChangePassword = true;
}
