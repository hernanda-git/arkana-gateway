using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnforceRequestLogAccountRestrict : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RequestLogs_ProviderAccounts_TenantId_RequestedProviderAcco~",
                table: "RequestLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_RequestLogs_ProviderAccounts_TenantId_ResolvedProviderAccou~",
                table: "RequestLogs");

            migrationBuilder.AddForeignKey(
                name: "FK_RequestLogs_ProviderAccounts_TenantId_RequestedProviderAcco~",
                table: "RequestLogs",
                columns: new[] { "TenantId", "RequestedProviderAccountId" },
                principalTable: "ProviderAccounts",
                principalColumns: new[] { "TenantId", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_RequestLogs_ProviderAccounts_TenantId_ResolvedProviderAccou~",
                table: "RequestLogs",
                columns: new[] { "TenantId", "ResolvedProviderAccountId" },
                principalTable: "ProviderAccounts",
                principalColumns: new[] { "TenantId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RequestLogs_ProviderAccounts_TenantId_RequestedProviderAcco~",
                table: "RequestLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_RequestLogs_ProviderAccounts_TenantId_ResolvedProviderAccou~",
                table: "RequestLogs");

            migrationBuilder.AddForeignKey(
                name: "FK_RequestLogs_ProviderAccounts_TenantId_RequestedProviderAcco~",
                table: "RequestLogs",
                columns: new[] { "TenantId", "RequestedProviderAccountId" },
                principalTable: "ProviderAccounts",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_RequestLogs_ProviderAccounts_TenantId_ResolvedProviderAccou~",
                table: "RequestLogs",
                columns: new[] { "TenantId", "ResolvedProviderAccountId" },
                principalTable: "ProviderAccounts",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.SetNull);
        }
    }
}
