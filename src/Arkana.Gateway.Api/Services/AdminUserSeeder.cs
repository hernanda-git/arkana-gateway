using Arkana.Domain.Entities;
using Arkana.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arkana.Gateway.Api.Services;

/// <summary>
/// Ensures a default admin user exists at startup and keeps its credentials in
/// sync with the environment variables ARKANA_ADMIN_USER / ARKANA_ADMIN_PASSWORD.
///
/// Idempotent and safe to call on every boot:
///   • No admin users at all   → create one from the env vars; a missing initial
///                               password is a fatal configuration error.
///   • Admin user already exists → if the configured password env var is set AND differs
///                               from the one currently stored, the password hash is
///                               refreshed. This lets an operator rotate the dashboard
///                               password via environment without manual DB edits, and
///                               replaces the insecure historical "admin"/"admin" default.
///
/// The default tenant (seeded via migration) is the owner of the admin user.
/// </summary>
public static class AdminUserSeeder
{
    public static async Task SeedAsync(IServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("AdminUserSeeder");

        // Ensure DashboardUsers table exists (no-op if already created via migration).
        await db.Database.MigrateAsync();

        var username = Environment.GetEnvironmentVariable("ARKANA_ADMIN_USER") ?? "admin";
        var password = Environment.GetEnvironmentVariable("ARKANA_ADMIN_PASSWORD");

        // Resolve the default tenant (seeded via migration). If it is missing we
        // cannot scope the user, so startup must fail closed.
        var defaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var tenant = await db.Tenants.FindAsync(defaultTenantId);
        if (tenant is null)
            throw new InvalidOperationException("Default tenant is missing; cannot seed the admin user.");

        var hasher = new PasswordHasher<DashboardUser>();

        var existing = await db.DashboardUsers
            .FirstOrDefaultAsync(u => u.Username == username);

        if (existing is null)
        {
            // No admin yet → require an operator-supplied initial secret. Never
            // create a known credential when deployment configuration is absent.
            if (string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException(
                    "ARKANA_ADMIN_PASSWORD is required when no admin user exists.");
            var seedPassword = password;
            var dummy = DashboardUser.Create("__internal__", "__internal__");
            var hash = hasher.HashPassword(dummy, seedPassword);
            var user = DashboardUser.Create(username, hash, "Admin");
            user.TenantId = tenant.Id;

            db.DashboardUsers.Add(user);
            await db.SaveChangesAsync();

            logger.LogInformation(
                "Seeded default admin user '{Username}' (id={Id}) for tenant '{Tenant}'.",
                user.Username, user.Id, tenant.Name);
            return;
        }

        // Admin exists. If a password is configured via env and differs from what
        // is currently stored, refresh the hash. We never leak/compare the plaintext
        // directly — we verify against the stored hash; only re-hash when a
        // verification fails (i.e. env was changed).
        if (!string.IsNullOrEmpty(password))
        {
            var verify = hasher.VerifyHashedPassword(existing, existing.PasswordHash, password);
            if (verify != PasswordVerificationResult.Success)
            {
                var dummy = DashboardUser.Create("__internal__", "__internal__");
                existing.PasswordHash = hasher.HashPassword(dummy, password);
                await db.SaveChangesAsync();
                logger.LogInformation(
                    "Admin user '{Username}' password refreshed from environment.",
                    existing.Username);
            }
        }
    }
}
