using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddModelsAndKeyModelPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Roles",
                table: "ApiKeys");

            migrationBuilder.CreateTable(
                name: "Models",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CostPerInputToken = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    CostPerOutputToken = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    MaxTokensPerRequest = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Models", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Models_AiProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "AiProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeyModels",
                columns: table => new
                {
                    AllowedByKeysId = table.Column<Guid>(type: "uuid", nullable: false),
                    AllowedModelsId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeyModels", x => new { x.AllowedByKeysId, x.AllowedModelsId });
                    table.ForeignKey(
                        name: "FK_ApiKeyModels_ApiKeys_AllowedByKeysId",
                        column: x => x.AllowedByKeysId,
                        principalTable: "ApiKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApiKeyModels_Models_AllowedModelsId",
                        column: x => x.AllowedModelsId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000001"),
                columns: new[] { "CostPerInputToken", "CostPerOutputToken" },
                values: new object[] { 0.00000014m, 0.00000028m });

            migrationBuilder.InsertData(
                table: "Models",
                columns: new[] { "Id", "Code", "CostPerInputToken", "CostPerOutputToken", "CreatedAt", "IsEnabled", "MaxTokensPerRequest", "Name", "ProviderId" },
                values: new object[,]
                {
                    { new Guid("b0000000-0000-0000-0000-000000000001"), "deepseek-v4-flash", 0.00000014m, 0.00000028m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, null, "DeepSeek V4 Flash", new Guid("a1000000-0000-0000-0000-000000000001") },
                    { new Guid("b0000000-0000-0000-0000-000000000002"), "deepseek-v4-pro", 0.00000174m, 0.00000348m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, null, "DeepSeek V4 Pro", new Guid("a1000000-0000-0000-0000-000000000001") },
                    { new Guid("b0000000-0000-0000-0000-000000000003"), "glm-5.1", 0.00000140m, 0.00000440m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, null, "GLM-5.1", new Guid("a1000000-0000-0000-0000-000000000001") },
                    { new Guid("b0000000-0000-0000-0000-000000000004"), "glm-5", 0.00000100m, 0.00000320m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, null, "GLM-5", new Guid("a1000000-0000-0000-0000-000000000001") },
                    { new Guid("b0000000-0000-0000-0000-000000000005"), "kimi-k2.5", 0.00000060m, 0.00000300m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, null, "Kimi K2.5", new Guid("a1000000-0000-0000-0000-000000000001") },
                    { new Guid("b0000000-0000-0000-0000-000000000006"), "kimi-k2.6", 0.00000095m, 0.00000400m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, null, "Kimi K2.6", new Guid("a1000000-0000-0000-0000-000000000001") },
                    { new Guid("b0000000-0000-0000-0000-000000000007"), "mimo-v2.5", 0.00000014m, 0.00000028m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, null, "MiMo V2.5", new Guid("a1000000-0000-0000-0000-000000000001") },
                    { new Guid("b0000000-0000-0000-0000-000000000008"), "mimo-v2.5-pro", 0.00000174m, 0.00000348m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, null, "MiMo V2.5 Pro", new Guid("a1000000-0000-0000-0000-000000000001") },
                    { new Guid("b0000000-0000-0000-0000-000000000011"), "gpt-4o", 0.0000025m, 0.00001m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "GPT-4o", new Guid("a1000000-0000-0000-0000-000000000002") },
                    { new Guid("b0000000-0000-0000-0000-000000000012"), "gpt-4o-mini", 0.00000015m, 0.0000006m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "GPT-4o Mini", new Guid("a1000000-0000-0000-0000-000000000002") },
                    { new Guid("b0000000-0000-0000-0000-000000000013"), "gpt-4.1", 0.000002m, 0.000008m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "GPT-4.1", new Guid("a1000000-0000-0000-0000-000000000002") },
                    { new Guid("b0000000-0000-0000-0000-000000000014"), "o3", 0.00001m, 0.00004m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "O3", new Guid("a1000000-0000-0000-0000-000000000002") },
                    { new Guid("b0000000-0000-0000-0000-000000000015"), "o4-mini", 0.0000011m, 0.0000044m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "O4 Mini", new Guid("a1000000-0000-0000-0000-000000000002") },
                    { new Guid("b0000000-0000-0000-0000-000000000021"), "gemini-2.5-pro", 0.00000125m, 0.000005m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "Gemini 2.5 Pro", new Guid("a1000000-0000-0000-0000-000000000003") },
                    { new Guid("b0000000-0000-0000-0000-000000000022"), "gemini-2.5-flash", 0.000000075m, 0.0000003m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "Gemini 2.5 Flash", new Guid("a1000000-0000-0000-0000-000000000003") },
                    { new Guid("b0000000-0000-0000-0000-000000000023"), "gemini-2.0-flash", 0.0000001m, 0.0000004m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "Gemini 2.0 Flash", new Guid("a1000000-0000-0000-0000-000000000003") },
                    { new Guid("b0000000-0000-0000-0000-000000000031"), "claude-sonnet-4", 0.000003m, 0.000015m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "Claude Sonnet 4", new Guid("a1000000-0000-0000-0000-000000000004") },
                    { new Guid("b0000000-0000-0000-0000-000000000032"), "claude-haiku-3.5", 0.0000008m, 0.000004m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "Claude Haiku 3.5", new Guid("a1000000-0000-0000-0000-000000000004") },
                    { new Guid("b0000000-0000-0000-0000-000000000033"), "claude-opus-4", 0.000015m, 0.000075m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "Claude Opus 4", new Guid("a1000000-0000-0000-0000-000000000004") },
                    { new Guid("b0000000-0000-0000-0000-000000000041"), "llama-3.1-8b", 0m, 0m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "Llama 3.1 8B", new Guid("a1000000-0000-0000-0000-000000000005") },
                    { new Guid("b0000000-0000-0000-0000-000000000042"), "llama-3.1-70b", 0m, 0m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "Llama 3.1 70B", new Guid("a1000000-0000-0000-0000-000000000005") },
                    { new Guid("b0000000-0000-0000-0000-000000000043"), "mistral", 0m, 0m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "Mistral", new Guid("a1000000-0000-0000-0000-000000000005") },
                    { new Guid("b0000000-0000-0000-0000-000000000044"), "codellama", 0m, 0m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "CodeLlama", new Guid("a1000000-0000-0000-0000-000000000005") }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyModels_AllowedModelsId",
                table: "ApiKeyModels",
                column: "AllowedModelsId");

            migrationBuilder.CreateIndex(
                name: "IX_Models_ProviderId_Code",
                table: "Models",
                columns: new[] { "ProviderId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeyModels");

            migrationBuilder.DropTable(
                name: "Models");

            migrationBuilder.AddColumn<string[]>(
                name: "Roles",
                table: "ApiKeys",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000001"),
                columns: new[] { "CostPerInputToken", "CostPerOutputToken" },
                values: new object[] { 0m, 0m });
        }
    }
}
