using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations;

// This migration was written without the generated Designer file, and without the attributes the
// Designer carries. EF Core discovers migrations through [Migration]/[DbContext], so the migration
// was invisible: databases that recorded it while it was still discoverable kept working, while a
// database created from scratch silently skipped it. A later data migration inserts
// ProviderAccounts.SupportedModels, so a fresh database failed at startup with
// "42703: column \"SupportedModels\" ... does not exist" and the gateway crash-looped.
// The attributes restore discovery; the column add is idempotent so a database that already has
// the column but no history row (a restored dump) can still record the migration.
[DbContext(typeof(GatewayDbContext))]
[Migration("20260902050000_AddProviderAccountSupportedModels")]
public partial class AddProviderAccountSupportedModels : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql(
            "ALTER TABLE \"ProviderAccounts\" ADD COLUMN IF NOT EXISTS \"SupportedModels\" character varying(2048);");

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql(
            "ALTER TABLE \"ProviderAccounts\" DROP COLUMN IF EXISTS \"SupportedModels\";");
}
