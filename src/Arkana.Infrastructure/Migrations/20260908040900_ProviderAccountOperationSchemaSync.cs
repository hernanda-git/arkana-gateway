using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProviderAccountOperationSchemaSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProviderAccountOperations_TenantId_ProviderAccountId_Kind",
                table: "ProviderAccountOperations");

            migrationBuilder.AlterColumn<string>(
                name: "Slot",
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
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);


            migrationBuilder.CreateIndex(
                name: "IX_ProviderAccountOperations_TenantId_ProviderAccountId_Kind",
                table: "ProviderAccountOperations",
                columns: new[] { "TenantId", "ProviderAccountId", "Kind" },
                unique: true,
                filter: "\"State\" IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProviderAccountOperations_TenantId_ProviderAccountId_Kind",
                table: "ProviderAccountOperations");


            migrationBuilder.AlterColumn<string>(
                name: "Slot",
                table: "ProviderAccountOperations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

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
                name: "IX_ProviderAccountOperations_TenantId_ProviderAccountId_Kind",
                table: "ProviderAccountOperations",
                columns: new[] { "TenantId", "ProviderAccountId", "Kind" },
                filter: "\"State\" IN (0, 1)");
        }
    }
}
