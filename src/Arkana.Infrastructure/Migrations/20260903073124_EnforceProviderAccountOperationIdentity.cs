using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations;

public partial class EnforceProviderAccountOperationIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("AuthorizationUrl", "ProviderAccountOperations", type: "character varying(2048)", maxLength: 2048, nullable: true);
        migrationBuilder.AddColumn<string>("IdempotencyKey", "ProviderAccountOperations", type: "character varying(128)", maxLength: 128, nullable: true);
        migrationBuilder.AddColumn<string>("RequestFingerprint", "ProviderAccountOperations", type: "character varying(256)", maxLength: 256, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<Guid>("ExpectedVersion", "ProviderAccountOperations", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>("ResultJson", "ProviderAccountOperations", type: "text", nullable: true);

        migrationBuilder.Sql("UPDATE \"ProviderAccountOperations\" SET \"IdempotencyKey\" = 'legacy-' || \"Id\"::text, \"RequestFingerprint\" = 'legacy-' || \"Id\"::text WHERE \"IdempotencyKey\" IS NULL OR \"RequestFingerprint\" = '';");

        migrationBuilder.CreateIndex("IX_ProviderAccountOperations_TenantId_ProviderAccountId_Kind", "ProviderAccountOperations", new[] { "TenantId", "ProviderAccountId", "Kind" }, unique: true, filter: "\"State\" IN (0, 1)");
        migrationBuilder.CreateIndex("IX_ProviderAccountOperations_TenantId_ProviderAccountId_Kind_I~", "ProviderAccountOperations", new[] { "TenantId", "ProviderAccountId", "Kind", "IdempotencyKey" }, unique: true, filter: "\"IdempotencyKey\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_ProviderAccountOperations_TenantId_ProviderAccountId_Kind", "ProviderAccountOperations");
        migrationBuilder.DropIndex("IX_ProviderAccountOperations_TenantId_ProviderAccountId_Kind_I~", "ProviderAccountOperations");
        migrationBuilder.DropColumn("AuthorizationUrl", "ProviderAccountOperations");
        migrationBuilder.DropColumn("IdempotencyKey", "ProviderAccountOperations");
        migrationBuilder.DropColumn("RequestFingerprint", "ProviderAccountOperations");
        migrationBuilder.DropColumn("ExpectedVersion", "ProviderAccountOperations");
        migrationBuilder.DropColumn("ResultJson", "ProviderAccountOperations");
    }
}
