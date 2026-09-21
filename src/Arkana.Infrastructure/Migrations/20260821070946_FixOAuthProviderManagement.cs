using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixOAuthProviderManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"AiProviders\" ADD COLUMN IF NOT EXISTS \"AccountId\" text;");

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000001"),
                column: "AccountId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000002"),
                column: "AccountId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000003"),
                column: "AccountId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000004"),
                column: "AccountId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000005"),
                column: "AccountId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000006"),
                column: "AccountId",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "AiProviders");
        }
    }
}
