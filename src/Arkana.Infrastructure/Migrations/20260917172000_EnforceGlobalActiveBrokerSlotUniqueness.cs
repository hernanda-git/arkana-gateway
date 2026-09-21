using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations;

/// <inheritdoc />
[DbContext(typeof(GatewayDbContext))]
[Migration("20260917172000_EnforceGlobalActiveBrokerSlotUniqueness")]
public partial class EnforceGlobalActiveBrokerSlotUniqueness : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Creating the unique index itself rejects any active duplicate slot and
        // rolls the migration back atomically, so no tenant is chosen as owner.
        migrationBuilder.DropIndex(
            name: "IX_ProviderAccounts_TenantId_BrokerInstanceId",
            table: "ProviderAccounts");

        migrationBuilder.CreateIndex(
            name: "IX_ProviderAccounts_BrokerInstanceId",
            table: "ProviderAccounts",
            column: "BrokerInstanceId",
            unique: true,
            filter: "\"BrokerInstanceId\" IS NOT NULL AND \"DeletedAt\" IS NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ProviderAccounts_BrokerInstanceId",
            table: "ProviderAccounts");

        migrationBuilder.CreateIndex(
            name: "IX_ProviderAccounts_TenantId_BrokerInstanceId",
            table: "ProviderAccounts",
            columns: new[] { "TenantId", "BrokerInstanceId" },
            unique: true,
            filter: "\"BrokerInstanceId\" IS NOT NULL AND \"DeletedAt\" IS NULL");
    }
}
