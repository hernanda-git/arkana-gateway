using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProviderAccountOperationIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProviderAccountOperations_TenantId_ProviderAccountId_Operat~",
                table: "ProviderAccountOperations");

            migrationBuilder.DropIndex(
                name: "IX_ProviderAccountOperations_TenantId_ProviderAccountId_Kind_I~",
                table: "ProviderAccountOperations");

            migrationBuilder.AlterColumn<string>(
                name: "IdempotencyKey",
                table: "ProviderAccountOperations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExpectedAccountVersion",
                table: "ProviderAccountOperations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AlterColumn<string>(
                name: "RequestFingerprint",
                table: "ProviderAccountOperations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            // Legacy rows had no client key/version. Give each row a deterministic,
            // collision-free identity so the new required columns can be indexed.
            migrationBuilder.Sql("UPDATE \"ProviderAccountOperations\" SET \"IdempotencyKey\" = 'legacy-' || \"Id\"::text WHERE \"IdempotencyKey\" = '';");
            migrationBuilder.Sql("UPDATE \"ProviderAccountOperations\" SET \"RequestFingerprint\" = md5(\"TenantId\"::text || '|' || \"ProviderAccountId\"::text || '|' || \"Kind\"::text || '|' || \"Slot\" || '|' || \"Id\"::text) WHERE \"RequestFingerprint\" = '';");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAccountOperations_TenantId_ProviderAccountId_Kind_I~",
                table: "ProviderAccountOperations",
                columns: new[] { "TenantId", "ProviderAccountId", "Kind", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProviderAccountOperations_TenantId_ProviderAccountId_Kind_I~",
                table: "ProviderAccountOperations");

            migrationBuilder.DropColumn(
                name: "ExpectedAccountVersion",
                table: "ProviderAccountOperations");

            migrationBuilder.AlterColumn<string>(
                name: "RequestFingerprint",
                table: "ProviderAccountOperations",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "IdempotencyKey",
                table: "ProviderAccountOperations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAccountOperations_TenantId_ProviderAccountId_Operat~",
                table: "ProviderAccountOperations",
                columns: new[] { "TenantId", "ProviderAccountId", "OperationGroup" },
                unique: true,
                filter: "\"State\" IN (0, 1)");
        }
    }
}
