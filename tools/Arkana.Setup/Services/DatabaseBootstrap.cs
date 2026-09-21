using Spectre.Console;
using Arkana.Setup.Models;
using Arkana.Setup.UI;
using Npgsql;
using System.Globalization;

namespace Arkana.Setup.Services;

/// <summary>Creates the database, applies EF Core migrations, and seeds initial data.</summary>
public static class DatabaseBootstrap
{
    public static async Task<bool> BootstrapAsync(SetupContext ctx)
    {
        AnsiConsole.Write(WizardRenderer.Section("Database Setup"));
        AnsiConsole.WriteLine();

        if (!await EnsureDatabaseExistsAsync(ctx))
            return false;

        if (!await ApplyMigrationsAsync(ctx))
            return false;

        WizardRenderer.Success("Seed data configured (auto-seeds on gateway start via AdminUserSeeder)");
        return true;
    }

    private static async Task<bool> EnsureDatabaseExistsAsync(SetupContext ctx)
    {
        return await AnsiConsole.Status()
            .StartAsync("Ensuring database exists...", async _ =>
            {
                try
                {
                    var masterCs = $"Host={EscapePgValue(ctx.DbHost)};Port={ctx.DbPort};Database=postgres;Username={EscapePgValue(ctx.DbUser)};Password={EscapePgPassword(ctx.DbPassword)}";
                    await using var conn = new NpgsqlConnection(masterCs);
                    await conn.OpenAsync();

                    // Check database exists (parameterized)
                    await using var checkCmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", conn);
                    checkCmd.Parameters.AddWithValue("@name", NpgsqlTypes.NpgsqlDbType.Text, ctx.DbName);
                    var exists = await checkCmd.ExecuteScalarAsync();

                    if (exists is null)
                    {
                        // CREATE DATABASE cannot run inside a transaction block
                        // Must close existing connection first
                        conn.Close();

                        await using var createConn = new NpgsqlConnection(masterCs);
                        await createConn.OpenAsync();

                        await using var createCmd = new NpgsqlCommand(
                            $"CREATE DATABASE \"{EscapePgIdentifier(ctx.DbName)}\"", createConn);
                        await createCmd.ExecuteNonQueryAsync();
                        WizardRenderer.Success($"Database '{ctx.DbName}' created");
                    }
                    else
                    {
                        WizardRenderer.Info($"Database '{ctx.DbName}' already exists");
                    }

                    // Create user if not exists (parameterized)
                    await using var userCheck = new NpgsqlCommand(
                        "SELECT 1 FROM pg_roles WHERE rolname = @user", conn);
                    userCheck.Parameters.AddWithValue("@user", NpgsqlTypes.NpgsqlDbType.Text, ctx.DbUser);
                    var userExists = await userCheck.ExecuteScalarAsync();

                    if (userExists is null)
                    {
                        conn.Close();
                        await using var createUserConn = new NpgsqlConnection(masterCs);
                        await createUserConn.OpenAsync();

                        await using var createUser = new NpgsqlCommand(
                            $"CREATE USER \"{EscapePgIdentifier(ctx.DbUser)}\" WITH PASSWORD @pwd", createUserConn);
                        createUser.Parameters.AddWithValue("@pwd", NpgsqlTypes.NpgsqlDbType.Text, ctx.DbPassword);
                        await createUser.ExecuteNonQueryAsync();

                        await using var grant = new NpgsqlCommand(
                            $"GRANT ALL PRIVILEGES ON DATABASE \"{EscapePgIdentifier(ctx.DbName)}\" TO \"{EscapePgIdentifier(ctx.DbUser)}\"", createUserConn);
                        await grant.ExecuteNonQueryAsync();
                        WizardRenderer.Success($"User '{ctx.DbUser}' created and granted access");
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    WizardRenderer.Error($"Database setup failed: {ex.Message}");
                    WizardRenderer.Info("You can also create the database manually:");
                    WizardRenderer.Info($"  sudo -u postgres createdb {ctx.DbName}");
                    return false;
                }
            });
    }

    private static async Task<bool> ApplyMigrationsAsync(SetupContext ctx)
    {
        return await AnsiConsole.Status()
            .StartAsync("Applying EF Core migrations...", async _ =>
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("dotnet",
                        $"ef database update " +
                        $"--project \"{Path.Combine(ctx.SolutionDir, "src", "Arkana.Infrastructure")}\" " +
                        $"--startup-project \"{Path.Combine(ctx.SolutionDir, "src", "Arkana.Gateway.Api")}\"")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = ctx.SolutionDir
                    };

                    var proc = System.Diagnostics.Process.Start(psi)!;
                    var output = await proc.StandardOutput.ReadToEndAsync();
                    var error = await proc.StandardError.ReadToEndAsync();
                    await proc.WaitForExitAsync();

                    if (proc.ExitCode != 0)
                    {
                        var errMsg = error.Trim();
                        if (errMsg.Length > 300) errMsg = errMsg[..300];
                        WizardRenderer.Error($"Migration failed: {errMsg}");
                        return false;
                    }

                    ctx.MigrationsApplied = true;
                    WizardRenderer.Success("Migrations applied");
                    return true;
                }
                catch (Exception ex)
                {
                    WizardRenderer.Error($"Migration error: {ex.Message}");
                    return false;
                }
            });
    }

    /// <summary>Escape a value for use in a PostgreSQL connection string (doubles quotes, strips semicolons).</summary>
    private static string EscapePgValue(string value)
        => value.Replace("\\", "\\\\").Replace("'", "\\'").Replace(";", "%3B");

    /// <summary>Escape a PostgreSQL identifier (double-quote-safe).</summary>
    private static string EscapePgIdentifier(string value)
        => value.Replace("\"", "\"\"");

    /// <summary>Escape a password for a PostgreSQL connection string (URL-style percent-encoding for special chars).</summary>
    private static string EscapePgPassword(string password)
    {
        // Only encode characters that would break a connection string
        var special = new HashSet<char>([';', '\\', '\'', '"', '=', ' ', '%']);
        return string.Create(password.Length * 2, (password, special), static (span, state) =>
        {
            var idx = 0;
            foreach (var c in state.password)
            {
                if (state.special.Contains(c))
                {
                    span[idx++] = '%';
                    var val = (int)c;
                    span[idx++] = (val >> 4).ToString("X", CultureInfo.InvariantCulture)[0];
                    span[idx++] = (val & 0xF).ToString("X", CultureInfo.InvariantCulture)[0];
                }
                else
                {
                    span[idx++] = c;
                }
            }
        });
    }
}
