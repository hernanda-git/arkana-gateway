using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace Arkana.Gateway.Api.Services;

/// <summary>
/// Dashboard user management — login, create, manage admin users.
/// Uses <see cref="PasswordHasher{T}"/> for credential hashing.
/// </summary>
public sealed class AuthService
{
    private readonly IDashboardUserRepository _repo;
    private readonly PasswordHasher<DashboardUser> _hasher = new();

    public AuthService(IDashboardUserRepository repo) => _repo = repo;

    /// <summary>Returns the user if credentials are valid, null otherwise.</summary>
    public async Task<DashboardUser?> ValidateAsync(string username, string password)
    {
        var user = await _repo.GetByUsernameAsync(username);
        if (user is null || !user.IsActive) return null;

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result == PasswordVerificationResult.Success ? user : null;
    }

    /// <summary>Create a new dashboard user with hashed password.</summary>
    public async Task<DashboardUser> CreateAsync(string username, string password, string role = "Admin")
    {
        // Create a throw-away instance for PasswordHasher (it ignores the user object
        // for HashPassword — only the password matters)
        var dummy = DashboardUser.Create("__internal__", "__internal__");
        var hash = _hasher.HashPassword(dummy, password);
        var user = DashboardUser.Create(username, hash, role);
        await _repo.AddAsync(user);
        return user;
    }

    /// <summary>Delete a user by id.</summary>
    public async Task DeleteAsync(Guid id) => await _repo.DeleteAsync(id);

    public Task<List<DashboardUser>> GetAllAsync() => _repo.GetAllAsync();

    /// <summary>Records a login event for the user.</summary>
    public async Task RecordLoginAsync(Guid userId)
    {
        var user = await _repo.GetByIdAsync(userId);
        if (user is null) return;
        user.RecordLogin();
        await _repo.UpdateAsync(user);
    }

    /// <summary>Returns users with the specified role.</summary>
    public async Task<List<DashboardUser>> GetByRoleAsync(string role)
    {
        var all = await _repo.GetAllAsync();
        return all.Where(u => u.Role == role).ToList();
    }

    /// <summary>Updates a user's role after validation.</summary>
    public async Task UpdateRoleAsync(Guid userId, string newRole)
    {
        if (!UserRoles.IsValidRole(newRole))
            throw new ArgumentException($"Invalid role '{newRole}'. Valid roles: {string.Join(", ", UserRoles.AllRoles)}", nameof(newRole));
        var user = await _repo.GetByIdAsync(userId);
        if (user is null) throw new InvalidOperationException($"User {userId} not found");
        user.Role = newRole;
        await _repo.UpdateAsync(user);
    }
}
