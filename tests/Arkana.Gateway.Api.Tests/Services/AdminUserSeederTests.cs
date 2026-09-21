using Arkana.Domain.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;

namespace Arkana.Gateway.Api.Tests.Services;

/// <summary>
/// Tests for the AdminUserSeeder logic — verifies password hashing and role assignment
/// without requiring a full EF Core DbContext (the actual seed method is integration-tested).
/// </summary>
public sealed class AdminUserSeederTests
{
    private readonly PasswordHasher<DashboardUser> _hasher = new();

    [Fact]
    public void SeedLogic_PasswordHashIsValid()
    {
        var dummy = DashboardUser.Create("__internal__", "__internal__");
        var hash = _hasher.HashPassword(dummy, "admin");

        var user = DashboardUser.Create("admin", hash, "Admin");

        user.Username.Should().Be("admin");
        user.Role.Should().Be(UserRoles.Admin);
        user.PasswordHash.Should().NotBeNullOrEmpty();
        user.PasswordHash.Should().NotBe("admin");

        // Verify the hash is correct
        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, "admin");
        result.Should().Be(PasswordVerificationResult.Success);
    }

    [Fact]
    public void SeedLogic_WrongPasswordFails()
    {
        var dummy = DashboardUser.Create("__internal__", "__internal__");
        var hash = _hasher.HashPassword(dummy, "correct_password");

        var user = DashboardUser.Create("admin", hash, "Admin");

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, "wrong_password");
        result.Should().Be(PasswordVerificationResult.Failed);
    }

    [Fact]
    public void SeedLogic_DefaultRoleIsAdmin()
    {
        var dummy = DashboardUser.Create("__internal__", "__internal__");
        var hash = _hasher.HashPassword(dummy, "password");

        var user = DashboardUser.Create("admin", hash);

        user.Role.Should().Be(UserRoles.Admin);
    }

    [Fact]
    public void SeedLogic_TenantIdCanBeSet()
    {
        var dummy = DashboardUser.Create("__internal__", "__internal__");
        var hash = _hasher.HashPassword(dummy, "password");

        var user = DashboardUser.Create("admin", hash, "Admin");
        var tenantId = Guid.NewGuid();
        user.TenantId = tenantId;

        user.TenantId.Should().Be(tenantId);
    }

    [Fact]
    public void SeedLogic_InvalidRoleThrows()
    {
        var dummy = DashboardUser.Create("__internal__", "__internal__");
        var hash = _hasher.HashPassword(dummy, "password");

        var act = () => DashboardUser.Create("admin", hash, "SuperAdmin");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Invalid role*");
    }
}
