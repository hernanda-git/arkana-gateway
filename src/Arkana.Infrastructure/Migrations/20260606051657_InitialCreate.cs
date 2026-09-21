using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BaseUrl = table.Column<string>(type: "text", nullable: true),
                    ApiKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MaxTokensPerRequest = table.Column<int>(type: "integer", nullable: true),
                    CostPerInputToken = table.Column<decimal>(type: "numeric", nullable: false),
                    CostPerOutputToken = table.Column<decimal>(type: "numeric", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Roles = table.Column<string[]>(type: "text[]", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "AiProviders",
                columns: new[] { "Id", "ApiKey", "BaseUrl", "Code", "CostPerInputToken", "CostPerOutputToken", "CreatedAt", "IsEnabled", "MaxTokensPerRequest", "Name", "Priority" },
                values: new object[,]
                {
                    { new Guid("a1000000-0000-0000-0000-000000000001"), null, "http://localhost:8080", "opencode", 0.00000014m, 0.00000028m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, null, "OpenCode", 0 },
                    { new Guid("a1000000-0000-0000-0000-000000000002"), null, null, "openai", 0.0000025m, 0.00001m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "OpenAI", 1 },
                    { new Guid("a1000000-0000-0000-0000-000000000003"), null, null, "gemini", 0.00000125m, 0.000005m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "Gemini", 1 },
                    { new Guid("a1000000-0000-0000-0000-000000000004"), null, null, "anthropic", 0.000003m, 0.000015m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "Anthropic", 2 },
                    { new Guid("a1000000-0000-0000-0000-000000000005"), null, "http://localhost:11434", "ollama", 0m, 0m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, null, "Ollama", 2 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiProviders_Code",
                table: "AiProviders",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_KeyHash",
                table: "ApiKeys",
                column: "KeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiProviders");

            migrationBuilder.DropTable(
                name: "ApiKeys");
        }
    }
}
