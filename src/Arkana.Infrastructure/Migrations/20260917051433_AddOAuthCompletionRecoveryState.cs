using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOAuthCompletionRecoveryState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletionExpiresAt",
                table: "OAuthPendingFlows",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletionRefreshExpiresAt",
                table: "OAuthPendingFlows",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletionTokenType",
                table: "OAuthPendingFlows",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SealedCompletionAccessToken",
                table: "OAuthPendingFlows",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SealedCompletionRefreshToken",
                table: "OAuthPendingFlows",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletionExpiresAt",
                table: "OAuthPendingFlows");

            migrationBuilder.DropColumn(
                name: "CompletionRefreshExpiresAt",
                table: "OAuthPendingFlows");

            migrationBuilder.DropColumn(
                name: "CompletionTokenType",
                table: "OAuthPendingFlows");

            migrationBuilder.DropColumn(
                name: "SealedCompletionAccessToken",
                table: "OAuthPendingFlows");

            migrationBuilder.DropColumn(
                name: "SealedCompletionRefreshToken",
                table: "OAuthPendingFlows");
        }
    }
}
