using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20260918093000_MarkOAuthPendingFlowCompletionFailure")]
public partial class MarkOAuthPendingFlowCompletionFailure : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CompletionFailure",
            table: "OAuthPendingFlows",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "CompletionFailedAt",
            table: "OAuthPendingFlows",
            type: "timestamp with time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CompletionFailure",
            table: "OAuthPendingFlows");

        migrationBuilder.DropColumn(
            name: "CompletionFailedAt",
            table: "OAuthPendingFlows");
    }
}
