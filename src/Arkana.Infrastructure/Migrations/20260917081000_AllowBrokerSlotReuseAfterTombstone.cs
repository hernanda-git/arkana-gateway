using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations;

/// <inheritdoc />
[DbContext(typeof(GatewayDbContext))]
[Migration("20260917081000_AllowBrokerSlotReuseAfterTombstone")]
public partial class AllowBrokerSlotReuseAfterTombstone : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ProviderAccounts_TenantId_BrokerInstanceId",
            table: "ProviderAccounts");

        migrationBuilder.CreateIndex(
            name: "IX_ProviderAccounts_TenantId_BrokerInstanceId",
            table: "ProviderAccounts",
            columns: new[] { "TenantId", "BrokerInstanceId" },
            unique: true,
            filter: "\"BrokerInstanceId\" IS NOT NULL AND \"DeletedAt\" IS NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ProviderAccounts_TenantId_BrokerInstanceId",
            table: "ProviderAccounts");

        migrationBuilder.CreateIndex(
            name: "IX_ProviderAccounts_TenantId_BrokerInstanceId",
            table: "ProviderAccounts",
            columns: new[] { "TenantId", "BrokerInstanceId" },
            unique: true,
            filter: "\"BrokerInstanceId\" IS NOT NULL");
    }
}
