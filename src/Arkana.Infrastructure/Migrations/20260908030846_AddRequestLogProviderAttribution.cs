using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations;

public partial class AddRequestLogProviderAttribution : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("RequestedProviderAccountCode", "RequestLogs", "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<Guid>("RequestedProviderAccountId", "RequestLogs", "uuid", nullable: true);
        migrationBuilder.AddColumn<string>("RequestedProviderCode", "RequestLogs", "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<string>("ResolvedProviderAccountCode", "RequestLogs", "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<Guid>("ResolvedProviderAccountId", "RequestLogs", "uuid", nullable: true);
        migrationBuilder.AddColumn<string>("RouteKind", "RequestLogs", "character varying(32)", maxLength: 32, nullable: false, defaultValue: "legacy");

        migrationBuilder.CreateIndex("IX_RequestLogs_RequestedProviderAccountId", "RequestLogs", "RequestedProviderAccountId");
        migrationBuilder.CreateIndex("IX_RequestLogs_ResolvedProviderAccountId", "RequestLogs", "ResolvedProviderAccountId");
        migrationBuilder.CreateIndex("IX_RequestLogs_TenantId_Provider_Timestamp", "RequestLogs", new[] { "TenantId", "Provider", "Timestamp" });
        migrationBuilder.CreateIndex("IX_RequestLogs_TenantId_ResolvedProviderAccountId", "RequestLogs", new[] { "TenantId", "ResolvedProviderAccountId" });

        migrationBuilder.AddForeignKey(
            name: "FK_RequestLogs_ProviderAccounts_RequestedProviderAccountId",
            table: "RequestLogs",
            column: "RequestedProviderAccountId",
            principalTable: "ProviderAccounts",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);
        migrationBuilder.AddForeignKey(
            name: "FK_RequestLogs_ProviderAccounts_ResolvedProviderAccountId",
            table: "RequestLogs",
            column: "ResolvedProviderAccountId",
            principalTable: "ProviderAccounts",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey("FK_RequestLogs_ProviderAccounts_RequestedProviderAccountId", "RequestLogs");
        migrationBuilder.DropForeignKey("FK_RequestLogs_ProviderAccounts_ResolvedProviderAccountId", "RequestLogs");
        migrationBuilder.DropIndex("IX_RequestLogs_RequestedProviderAccountId", "RequestLogs");
        migrationBuilder.DropIndex("IX_RequestLogs_ResolvedProviderAccountId", "RequestLogs");
        migrationBuilder.DropIndex("IX_RequestLogs_TenantId_Provider_Timestamp", "RequestLogs");
        migrationBuilder.DropIndex("IX_RequestLogs_TenantId_ResolvedProviderAccountId", "RequestLogs");
        migrationBuilder.DropColumn("RequestedProviderAccountCode", "RequestLogs");
        migrationBuilder.DropColumn("RequestedProviderAccountId", "RequestLogs");
        migrationBuilder.DropColumn("RequestedProviderCode", "RequestLogs");
        migrationBuilder.DropColumn("ResolvedProviderAccountCode", "RequestLogs");
        migrationBuilder.DropColumn("ResolvedProviderAccountId", "RequestLogs");
        migrationBuilder.DropColumn("RouteKind", "RequestLogs");
    }
}
