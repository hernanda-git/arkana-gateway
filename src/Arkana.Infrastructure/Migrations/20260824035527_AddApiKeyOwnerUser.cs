using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyOwnerUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "ApiKeys",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_OwnerUserId",
                table: "ApiKeys",
                column: "OwnerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_ApiKeys_DashboardUsers_OwnerUserId",
                table: "ApiKeys",
                column: "OwnerUserId",
                principalTable: "DashboardUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApiKeys_DashboardUsers_OwnerUserId",
                table: "ApiKeys");

            migrationBuilder.DropIndex(
                name: "IX_ApiKeys_OwnerUserId",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "ApiKeys");
        }
    }
}
