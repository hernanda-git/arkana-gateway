using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProviderAccountConsistency : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_ProviderAccountOperations_Nonce", table: "ProviderAccountOperations");
            migrationBuilder.DropIndex(name: "IX_ProviderAccountOperations_TenantId_ProviderAccountId_Kind_S~", table: "ProviderAccountOperations");

            migrationBuilder.AddColumn<string>(name: "AuditActor", table: "ProviderAccountOperations", type: "character varying(256)", maxLength: 256, nullable: true);
            migrationBuilder.AddColumn<string>(name: "FailureClass", table: "ProviderAccountOperations", type: "character varying(128)", maxLength: 128, nullable: true);
            // IdempotencyKey is introduced by the preceding identity migration.
            migrationBuilder.AddColumn<string>(name: "OperationGroup", table: "ProviderAccountOperations", type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "");

            migrationBuilder.CreateIndex(name: "IX_ProviderAccountOperations_Nonce", table: "ProviderAccountOperations", column: "Nonce", unique: true);
            migrationBuilder.Sql("UPDATE \"ProviderAccountOperations\" SET \"OperationGroup\" = CASE WHEN \"Kind\" IN (0, 3) THEN 'oauth' ELSE lower(\"Kind\"::text) END WHERE \"OperationGroup\" = '';");
            migrationBuilder.CreateIndex(name: "IX_ProviderAccountOperations_TenantId_ProviderAccountId_Operat~", table: "ProviderAccountOperations", columns: new[] { "TenantId", "ProviderAccountId", "OperationGroup" }, unique: true, filter: "\"State\" IN (0, 1)");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_ProviderAccountOperations_TenantId_ProviderAccountId_Operat~", table: "ProviderAccountOperations");
            migrationBuilder.DropColumn(name: "AuditActor", table: "ProviderAccountOperations");
            migrationBuilder.DropColumn(name: "FailureClass", table: "ProviderAccountOperations");
            migrationBuilder.DropColumn(name: "OperationGroup", table: "ProviderAccountOperations");
            migrationBuilder.CreateIndex(name: "IX_ProviderAccountOperations_Nonce", table: "ProviderAccountOperations", column: "Nonce", unique: true);
            migrationBuilder.CreateIndex(name: "IX_ProviderAccountOperations_TenantId_ProviderAccountId_Kind_S~", table: "ProviderAccountOperations", columns: new[] { "TenantId", "ProviderAccountId", "Kind", "State" });
        }
    }
}
