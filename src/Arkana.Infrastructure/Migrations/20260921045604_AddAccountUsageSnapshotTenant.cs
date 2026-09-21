using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountUsageSnapshotTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AccountUsageSnapshots_AccountCode",
                table: "AccountUsageSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_AccountUsageSnapshots_AccountProviderId_WindowKind",
                table: "AccountUsageSnapshots");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AccountUsageSnapshots",
                type: "uuid",
                nullable: true);

            // Snapshots are a cache; derive ownership from the authoritative
            // provider row before making tenant scope mandatory. Orphaned cache
            // rows are safe to discard and will be repopulated by inference.
            migrationBuilder.Sql("""
                UPDATE "AccountUsageSnapshots" AS s
                SET "TenantId" = p."TenantId"
                FROM "AiProviders" AS p
                WHERE p."Id" = s."AccountProviderId";
                DELETE FROM "AccountUsageSnapshots" WHERE "TenantId" IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "AccountUsageSnapshots",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountUsageSnapshots_TenantId_AccountCode",
                table: "AccountUsageSnapshots",
                columns: new[] { "TenantId", "AccountCode" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountUsageSnapshots_TenantId_AccountProviderId_WindowKind",
                table: "AccountUsageSnapshots",
                columns: new[] { "TenantId", "AccountProviderId", "WindowKind" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountUsageSnapshots_Tenants_TenantId",
                table: "AccountUsageSnapshots",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccountUsageSnapshots_Tenants_TenantId",
                table: "AccountUsageSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_AccountUsageSnapshots_TenantId_AccountCode",
                table: "AccountUsageSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_AccountUsageSnapshots_TenantId_AccountProviderId_WindowKind",
                table: "AccountUsageSnapshots");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AccountUsageSnapshots");

            migrationBuilder.CreateIndex(
                name: "IX_AccountUsageSnapshots_AccountCode",
                table: "AccountUsageSnapshots",
                column: "AccountCode");

            migrationBuilder.CreateIndex(
                name: "IX_AccountUsageSnapshots_AccountProviderId_WindowKind",
                table: "AccountUsageSnapshots",
                columns: new[] { "AccountProviderId", "WindowKind" },
                unique: true);
        }
    }
}
