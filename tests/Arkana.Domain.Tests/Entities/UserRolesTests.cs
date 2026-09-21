using Arkana.Domain.Entities;
using FluentAssertions;

namespace Arkana.Domain.Tests.Entities;

public sealed class UserRolesTests
{
    [Fact]
    public void IsValidRole_Admin_ReturnsTrue() => UserRoles.IsValidRole(UserRoles.Admin).Should().BeTrue();

    [Fact]
    public void IsValidRole_User_ReturnsTrue() => UserRoles.IsValidRole(UserRoles.User).Should().BeTrue();

    [Fact]
    public void IsValidRole_Portal_ReturnsTrue() => UserRoles.IsValidRole(UserRoles.Portal).Should().BeTrue();

    [Fact]
    public void IsValidRole_InvalidRole_ReturnsFalse() => UserRoles.IsValidRole("SuperAdmin").Should().BeFalse();

    [Fact]
    public void IsValidRole_EmptyString_ReturnsFalse() => UserRoles.IsValidRole("").Should().BeFalse();

    [Fact]
    public void AllRoles_ContainsThreeRoles() => UserRoles.AllRoles.Should().HaveCount(3);

    [Fact]
    public void Create_ValidRole_Succeeds()
    {
        var user = DashboardUser.Create("test", "hash", UserRoles.User);
        user.Role.Should().Be(UserRoles.User);
    }

    [Fact]
    public void Create_InvalidRole_ThrowsArgumentException()
    {
        var act = () => DashboardUser.Create("test", "hash", "Invalid");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Invalid role*");
    }

    [Fact]
    public void Create_DefaultRole_IsAdmin()
    {
        var user = DashboardUser.Create("test", "hash");
        user.Role.Should().Be(UserRoles.Admin);
    }

    [Fact]
    public void Create_WithEmail_SetsEmail()
    {
        var user = DashboardUser.Create("test", "hash", UserRoles.Admin, "test@example.com");
        user.Email.Should().Be("test@example.com");
    }

    [Fact]
    public void RecordLogin_UpdatesLastLoginAt()
    {
        var user = DashboardUser.Create("test", "hash");
        user.RecordLogin();
        user.LastLoginAt.Should().NotBeNull();
        user.LoginCount.Should().Be(1);
    }

    [Fact]
    public void RecordLogin_IncrementsCount()
    {
        var user = DashboardUser.Create("test", "hash");
        user.RecordLogin();
        user.RecordLogin();
        user.LoginCount.Should().Be(2);
    }
}
