using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations;

/// <inheritdoc />
[DbContext(typeof(GatewayDbContext))]
[Migration("20260917180000_EnforceCaseInsensitiveBrokerSlotUniqueness")]
public partial class EnforceCaseInsensitiveBrokerSlotUniqueness : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "BrokerInstanceIdNormalized",
            table: "ProviderAccounts",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true,
            computedColumnSql: "lower(\"BrokerInstanceId\")",
            stored: true);

        migrationBuilder.DropIndex(
            name: "IX_ProviderAccounts_BrokerInstanceId",
            table: "ProviderAccounts");

        // The unique index rejects any active case-insensitive duplicate and
        // rolls the migration back atomically instead of choosing an owner.
        migrationBuilder.CreateIndex(
            name: "IX_ProviderAccounts_BrokerInstanceIdNormalized",
            table: "ProviderAccounts",
            column: "BrokerInstanceIdNormalized",
            unique: true,
            filter: "\"BrokerInstanceId\" IS NOT NULL AND \"DeletedAt\" IS NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ProviderAccounts_BrokerInstanceIdNormalized",
            table: "ProviderAccounts");

        migrationBuilder.DropColumn(
            name: "BrokerInstanceIdNormalized",
            table: "ProviderAccounts");

        migrationBuilder.CreateIndex(
            name: "IX_ProviderAccounts_BrokerInstanceId",
            table: "ProviderAccounts",
            column: "BrokerInstanceId",
            unique: true,
            filter: "\"BrokerInstanceId\" IS NOT NULL AND \"DeletedAt\" IS NULL");
    }
}
