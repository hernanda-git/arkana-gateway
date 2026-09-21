using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnterprisePhase4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "TokenUsages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "RequestLogs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Models",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "DashboardUsers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "ApiKeys",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AiProviders",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "Plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MonthlyPrice = table.Column<decimal>(type: "numeric(20,4)", nullable: false),
                    IncludedInputTokens = table.Column<long>(type: "bigint", nullable: false),
                    IncludedOutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    MaxRequestsPerMinute = table.Column<int>(type: "integer", nullable: false),
                    MaxTokensPerMinute = table.Column<int>(type: "integer", nullable: false),
                    MaxConcurrent = table.Column<int>(type: "integer", nullable: false),
                    MaxApiKeys = table.Column<int>(type: "integer", nullable: false),
                    Features = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Slug = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Settings = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantBudgets",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MonthlyInputTokenCap = table.Column<long>(type: "bigint", nullable: false),
                    MonthlyOutputTokenCap = table.Column<long>(type: "bigint", nullable: false),
                    RemainingInputTokens = table.Column<long>(type: "bigint", nullable: false),
                    RemainingOutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantBudgets", x => x.TenantId);
                    table.ForeignKey(
                        name: "FK_TenantBudgets_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantPlans",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OverrideIncludedInputTokens = table.Column<long>(type: "bigint", nullable: true),
                    OverrideIncludedOutputTokens = table.Column<long>(type: "bigint", nullable: true),
                    OverrideMaxRpm = table.Column<int>(type: "integer", nullable: true),
                    OverrideMaxTpm = table.Column<int>(type: "integer", nullable: true),
                    OverrideMaxConcurrent = table.Column<int>(type: "integer", nullable: true),
                    OverrideMaxApiKeys = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantPlans", x => x.TenantId);
                    table.ForeignKey(
                        name: "FK_TenantPlans_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantPlans_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

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

            migrationBuilder.InsertData(
                table: "Plans",
                columns: new[] { "Id", "Features", "IncludedInputTokens", "IncludedOutputTokens", "IsActive", "MaxApiKeys", "MaxConcurrent", "MaxRequestsPerMinute", "MaxTokensPerMinute", "MonthlyPrice", "Name", "Slug" },
                values: new object[,]
                {
                    { new Guid("f0000000-0000-0000-0000-000000000001"), "{\"semantic_cache\":false,\"streaming\":true,\"embeddings\":false}", 0L, 0L, true, 2, 2, 10, 100000, 0m, "Free", "free" },
                    { new Guid("f0000000-0000-0000-0000-000000000002"), "{\"semantic_cache\":true,\"streaming\":true,\"embeddings\":true}", 1000000L, 500000L, true, 10, 5, 60, 1000000, 29.99m, "Pro", "pro" },
                    { new Guid("f0000000-0000-0000-0000-000000000003"), "{\"semantic_cache\":true,\"streaming\":true,\"embeddings\":true}", 10000000L, 5000000L, true, 100, 25, 300, 10000000, 199.99m, "Enterprise", "enterprise" }
                });

            migrationBuilder.InsertData(
                table: "Tenants",
                columns: new[] { "Id", "CreatedAt", "IsActive", "Name", "Settings", "Slug" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000001"), new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, "Default Tenant", null, "default" });

            migrationBuilder.InsertData(
                table: "TenantBudgets",
                columns: new[] { "TenantId", "MonthlyInputTokenCap", "MonthlyOutputTokenCap", "PeriodEnd", "PeriodStart", "RemainingInputTokens", "RemainingOutputTokens" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000001"), 1000000L, 500000L, new DateTimeOffset(new DateTime(2026, 7, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2026, 6, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), 1000000L, 500000L });

            migrationBuilder.InsertData(
                table: "TenantPlans",
                columns: new[] { "TenantId", "EndsAt", "OverrideIncludedInputTokens", "OverrideIncludedOutputTokens", "OverrideMaxApiKeys", "OverrideMaxConcurrent", "OverrideMaxRpm", "OverrideMaxTpm", "PlanId", "StartsAt" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000001"), null, null, null, null, null, null, null, new Guid("f0000000-0000-0000-0000-000000000002"), new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.CreateIndex(
                name: "IX_TokenUsages_TenantId",
                table: "TokenUsages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_TenantId",
                table: "RequestLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Models_TenantId",
                table: "Models",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardUsers_TenantId",
                table: "DashboardUsers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_TenantId",
                table: "ApiKeys",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AiProviders_TenantId",
                table: "AiProviders",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Plans_Slug",
                table: "Plans",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantPlans_PlanId",
                table: "TenantPlans",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                table: "Tenants",
                column: "Slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AiProviders_Tenants_TenantId",
                table: "AiProviders",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ApiKeys_Tenants_TenantId",
                table: "ApiKeys",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DashboardUsers_Tenants_TenantId",
                table: "DashboardUsers",
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AiProviders_Tenants_TenantId",
                table: "AiProviders");

            migrationBuilder.DropForeignKey(
                name: "FK_ApiKeys_Tenants_TenantId",
                table: "ApiKeys");

            migrationBuilder.DropForeignKey(
                name: "FK_DashboardUsers_Tenants_TenantId",
                table: "DashboardUsers");

            migrationBuilder.DropForeignKey(
                name: "FK_Models_Tenants_TenantId",
                table: "Models");

            migrationBuilder.DropTable(
                name: "TenantBudgets");

            migrationBuilder.DropTable(
                name: "TenantPlans");

            migrationBuilder.DropTable(
                name: "Plans");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropIndex(
                name: "IX_TokenUsages_TenantId",
                table: "TokenUsages");

            migrationBuilder.DropIndex(
                name: "IX_RequestLogs_TenantId",
                table: "RequestLogs");

            migrationBuilder.DropIndex(
                name: "IX_Models_TenantId",
                table: "Models");

            migrationBuilder.DropIndex(
                name: "IX_DashboardUsers_TenantId",
                table: "DashboardUsers");

            migrationBuilder.DropIndex(
                name: "IX_ApiKeys_TenantId",
                table: "ApiKeys");

            migrationBuilder.DropIndex(
                name: "IX_AiProviders_TenantId",
                table: "AiProviders");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "TokenUsages");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "RequestLogs");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "DashboardUsers");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AiProviders");
        }
    }
}
