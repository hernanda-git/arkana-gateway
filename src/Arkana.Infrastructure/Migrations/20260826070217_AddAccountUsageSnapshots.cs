using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.src.Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountUsageSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountUsageSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WindowKind = table.Column<int>(type: "integer", nullable: false),
                    UsedPercent = table.Column<double>(type: "double precision", nullable: false),
                    WindowMinutes = table.Column<int>(type: "integer", nullable: true),
                    ResetsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PlanType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountUsageSnapshots", x => x.Id);
                });

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountUsageSnapshots");
        }
    }
}
