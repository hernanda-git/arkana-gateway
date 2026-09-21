using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnforceTenantScopedRequestLogAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RequestLogs_ProviderAccounts_RequestedProviderAccountId",
                table: "RequestLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_RequestLogs_ProviderAccounts_ResolvedProviderAccountId",
                table: "RequestLogs");

            migrationBuilder.DropIndex(
                name: "IX_RequestLogs_RequestedProviderAccountId",
                table: "RequestLogs");

            migrationBuilder.DropIndex(
                name: "IX_RequestLogs_ResolvedProviderAccountId",
                table: "RequestLogs");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ProviderAccounts_TenantId_Id",
                table: "ProviderAccounts",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_TenantId_RequestedProviderAccountId",
                table: "RequestLogs",
                columns: new[] { "TenantId", "RequestedProviderAccountId" });

            migrationBuilder.AddForeignKey(
                name: "FK_RequestLogs_ProviderAccounts_TenantId_RequestedProviderAcco~",
                table: "RequestLogs",
                columns: new[] { "TenantId", "RequestedProviderAccountId" },
                principalTable: "ProviderAccounts",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: "FK_RequestLogs_ProviderAccounts_TenantId_ResolvedProviderAccou~",
                table: "RequestLogs",
                columns: new[] { "TenantId", "ResolvedProviderAccountId" },
                principalTable: "ProviderAccounts",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.NoAction);
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

            migrationBuilder.DropIndex(
                name: "IX_RequestLogs_TenantId_RequestedProviderAccountId",
                table: "RequestLogs");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ProviderAccounts_TenantId_Id",
                table: "ProviderAccounts");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_RequestedProviderAccountId",
                table: "RequestLogs",
                column: "RequestedProviderAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_ResolvedProviderAccountId",
                table: "RequestLogs",
                column: "ResolvedProviderAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_RequestLogs_ProviderAccounts_RequestedProviderAccountId",
                table: "RequestLogs",
                column: "RequestedProviderAccountId",
                principalTable: "ProviderAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_RequestLogs_ProviderAccounts_ResolvedProviderAccountId",
                table: "RequestLogs",
                column: "ResolvedProviderAccountId",
                principalTable: "ProviderAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
