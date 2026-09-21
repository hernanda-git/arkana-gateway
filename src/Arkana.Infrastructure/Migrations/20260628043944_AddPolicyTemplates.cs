using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPolicyTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AiProviders_Tenants_TenantId",
                table: "AiProviders");

            migrationBuilder.DropForeignKey(
                name: "FK_Models_Tenants_TenantId",
                table: "Models");

            migrationBuilder.DropIndex(
                name: "IX_Models_TenantId",
                table: "Models");

            migrationBuilder.DropIndex(
                name: "IX_AiProviders_TenantId",
                table: "AiProviders");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AiProviders");

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "DashboardUsers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastLoginAt",
                table: "DashboardUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LoginCount",
                table: "DashboardUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PolicyTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Slug = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Config = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsBuiltin = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyTemplates_IsActive",
                table: "PolicyTemplates",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyTemplates_Slug",
                table: "PolicyTemplates",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PolicyTemplates");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "DashboardUsers");

            migrationBuilder.DropColumn(
                name: "LastLoginAt",
                table: "DashboardUsers");

            migrationBuilder.DropColumn(
                name: "LoginCount",
                table: "DashboardUsers");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Models",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AiProviders",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000001"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000002"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000003"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000004"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000005"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000006"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000001"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000002"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000003"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000004"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000005"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000006"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000007"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000008"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000011"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000012"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000013"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000014"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000015"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000021"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000022"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000023"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000031"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000032"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000033"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000041"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000042"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000043"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000044"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000051"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000052"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.CreateIndex(
                name: "IX_Models_TenantId",
                table: "Models",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AiProviders_TenantId",
                table: "AiProviders",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_AiProviders_Tenants_TenantId",
                table: "AiProviders",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Models_Tenants_TenantId",
                table: "Models",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
