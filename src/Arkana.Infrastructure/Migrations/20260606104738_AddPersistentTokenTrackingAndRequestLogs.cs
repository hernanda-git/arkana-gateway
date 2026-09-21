using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPersistentTokenTrackingAndRequestLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RequestLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ApiKeyName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    MessagesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ResponseContent = table.Column<string>(type: "text", nullable: true),
                    ToolCallsJson = table.Column<string>(type: "jsonb", nullable: true),
                    InputTokens = table.Column<int>(type: "integer", nullable: false),
                    OutputTokens = table.Column<int>(type: "integer", nullable: false),
                    Cost = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    DurationTicks = table.Column<long>(type: "bigint", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsError = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TokenUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    InputTokens = table.Column<int>(type: "integer", nullable: false),
                    OutputTokens = table.Column<int>(type: "integer", nullable: false),
                    Cost = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    DurationTicks = table.Column<long>(type: "bigint", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApiKeyName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TokenUsages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_Model",
                table: "RequestLogs",
                column: "Model");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_Provider",
                table: "RequestLogs",
                column: "Provider");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_Timestamp",
                table: "RequestLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_TokenUsages_Model",
                table: "TokenUsages",
                column: "Model");

            migrationBuilder.CreateIndex(
                name: "IX_TokenUsages_Provider",
                table: "TokenUsages",
                column: "Provider");

            migrationBuilder.CreateIndex(
                name: "IX_TokenUsages_Timestamp",
                table: "TokenUsages",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RequestLogs");

            migrationBuilder.DropTable(
                name: "TokenUsages");
        }
    }
}
