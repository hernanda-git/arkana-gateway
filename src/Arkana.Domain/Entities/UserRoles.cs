namespace Arkana.Domain.Entities;

/// <summary>
/// Role constants for dashboard users.
/// </summary>
public static class UserRoles
{
    public const string Admin = "Admin";
    public const string User = "User";
    public const string Portal = "Portal";

    public static bool IsValidRole(string role) => role is Admin or User or Portal;

    public static IReadOnlyList<string> AllRoles => [Admin, User, Portal];
}
