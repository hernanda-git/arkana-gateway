using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20260918090000_AddOAuthPendingFlowCompletionClaim")]
public partial class AddOAuthPendingFlowCompletionClaim : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "CompletionClaimedAt",
            table: "OAuthPendingFlows",
            type: "timestamp with time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CompletionClaimedAt",
            table: "OAuthPendingFlows");
    }
}
