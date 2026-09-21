using Microsoft.EntityFrameworkCore.Migrations;
using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace Arkana.Infrastructure.Migrations;

/// <inheritdoc />
[Migration("20260917041500_AddProviderAccountOperationProviderAccountForeignKey")]
[DbContext(typeof(GatewayDbContext))]
public partial class AddProviderAccountOperationProviderAccountForeignKey : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM "ProviderAccountOperations" AS o
            WHERE NOT EXISTS (
                SELECT 1
                FROM "ProviderAccounts" AS a
                WHERE a."TenantId" = o."TenantId"
                  AND a."Id" = o."ProviderAccountId");
            """);

        migrationBuilder.AddForeignKey(
            name: "FK_ProviderAccountOperations_ProviderAccounts_TenantAccount",
            table: "ProviderAccountOperations",
            columns: new[] { "TenantId", "ProviderAccountId" },
            principalTable: "ProviderAccounts",
            principalColumns: new[] { "TenantId", "Id" },
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_ProviderAccountOperations_ProviderAccounts_TenantAccount",
            table: "ProviderAccountOperations");
    }
}
