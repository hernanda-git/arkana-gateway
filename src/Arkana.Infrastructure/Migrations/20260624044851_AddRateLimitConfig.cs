using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRateLimitConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RateLimitMaxConcurrent",
                table: "ApiKeys",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RateLimitRpm",
                table: "ApiKeys",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RateLimitTpm",
                table: "ApiKeys",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GlobalRateLimits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    DefaultRequestsPerMinute = table.Column<int>(type: "integer", nullable: false, defaultValue: 60),
                    DefaultTokensPerMinute = table.Column<int>(type: "integer", nullable: false, defaultValue: 1000000),
                    DefaultMaxConcurrent = table.Column<int>(type: "integer", nullable: false, defaultValue: 5),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalRateLimits", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GlobalRateLimits");

            migrationBuilder.DropColumn(
                name: "RateLimitMaxConcurrent",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "RateLimitRpm",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "RateLimitTpm",
                table: "ApiKeys");
        }
    }
}
