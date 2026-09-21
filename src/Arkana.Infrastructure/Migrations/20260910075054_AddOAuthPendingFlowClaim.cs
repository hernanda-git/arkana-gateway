using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOAuthPendingFlowClaim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimedAt",
                table: "OAuthPendingFlows",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "OAuthPendingFlows");
        }
    }
}
