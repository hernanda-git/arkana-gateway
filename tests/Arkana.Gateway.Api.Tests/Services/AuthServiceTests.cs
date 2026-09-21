using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Gateway.Api.Services;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace Arkana.Gateway.Api.Tests.Services;

public sealed class AuthServiceTests
{
    private readonly IDashboardUserRepository _repo = Substitute.For<IDashboardUserRepository>();
    private readonly AuthService _sut;

    public AuthServiceTests()
    {
        _sut = new AuthService(_repo);
    }

    [Fact]
    public async Task ValidateAsync_ValidCredentials_ReturnsUser()
    {
        var user = DashboardUser.Create("admin", "placeholder");
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<DashboardUser>();
        user.PasswordHash = hasher.HashPassword(user, "password123");
        _repo.GetByUsernameAsync("admin").Returns(user);

        var result = await _sut.ValidateAsync("admin", "password123");

        result.Should().NotBeNull();
        result!.Username.Should().Be("admin");
    }

    [Fact]
    public async Task ValidateAsync_InvalidPassword_ReturnsNull()
    {
        var user = DashboardUser.Create("admin", "placeholder");
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<DashboardUser>();
        user.PasswordHash = hasher.HashPassword(user, "correct_password");
        _repo.GetByUsernameAsync("admin").Returns(user);

        var result = await _sut.ValidateAsync("admin", "wrong_password");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateAsync_NonexistentUser_ReturnsNull()
    {
        _repo.GetByUsernameAsync("nobody").ReturnsNull();

        var result = await _sut.ValidateAsync("nobody", "password");

        result.Should().BeNull();
    }

    [Fact]
    public async Task RecordLoginAsync_ValidUser_IncrementsCount()
    {
        var user = DashboardUser.Create("admin", "hash");
        _repo.GetByIdAsync(user.Id).Returns(user);

        await _sut.RecordLoginAsync(user.Id);

        user.LoginCount.Should().Be(1);
        user.LastLoginAt.Should().NotBeNull();
        await _repo.Received(1).UpdateAsync(user);
    }

    [Fact]
    public async Task RecordLoginAsync_NonexistentUser_DoesNotThrow()
    {
        _repo.GetByIdAsync(Arg.Any<Guid>()).ReturnsNull();

        var act = async () => await _sut.RecordLoginAsync(Guid.NewGuid());
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetByRoleAsync_AdminRole_ReturnsAdminUsers()
    {
        var admin = DashboardUser.Create("admin", "hash", UserRoles.Admin);
        var user = DashboardUser.Create("user1", "hash", UserRoles.User);
        _repo.GetAllAsync().Returns(new List<DashboardUser> { admin, user });

        var result = await _sut.GetByRoleAsync(UserRoles.Admin);

        result.Should().HaveCount(1);
        result[0].Username.Should().Be("admin");
    }

    [Fact]
    public async Task UpdateRoleAsync_ValidRole_UpdatesUser()
    {
        var user = DashboardUser.Create("user1", "hash", UserRoles.User);
        _repo.GetByIdAsync(user.Id).Returns(user);

        await _sut.UpdateRoleAsync(user.Id, UserRoles.Admin);

        user.Role.Should().Be(UserRoles.Admin);
        await _repo.Received(1).UpdateAsync(user);
    }

    [Fact]
    public async Task UpdateRoleAsync_InvalidRole_ThrowsArgumentException()
    {
        var act = () => _sut.UpdateRoleAsync(Guid.NewGuid(), "SuperAdmin");
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Invalid role*");
    }

    [Fact]
    public async Task UpdateRoleAsync_NonexistentUser_ThrowsInvalidOperationException()
    {
        _repo.GetByIdAsync(Arg.Any<Guid>()).ReturnsNull();

        var act = () => _sut.UpdateRoleAsync(Guid.NewGuid(), UserRoles.Admin);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
