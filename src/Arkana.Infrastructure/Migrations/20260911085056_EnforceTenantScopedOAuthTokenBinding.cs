using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnforceTenantScopedOAuthTokenBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Legacy rows were created before tenant binding was enforced. A zero
            // tenant can be safely backfilled from the owning provider because
            // AiProvider.Id was already globally unique. Any non-zero mismatch or
            // duplicate is ambiguous and must stop the migration rather than
            // attaching credentials to the wrong tenant.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    UPDATE "ProviderOAuthTokens" AS token
                    SET "TenantId" = provider."TenantId"
                    FROM "AiProviders" AS provider
                    WHERE token."AiProviderId" = provider."Id"
                      AND token."TenantId" = '00000000-0000-0000-0000-000000000000';

                    IF EXISTS (
                        SELECT 1
                        FROM "ProviderOAuthTokens" AS token
                        JOIN "AiProviders" AS provider ON provider."Id" = token."AiProviderId"
                        WHERE token."TenantId" <> provider."TenantId"
                    ) THEN
                        RAISE EXCEPTION 'ProviderOAuthTokens contains tenant/provider mismatches; repair before release';
                    END IF;

                    IF EXISTS (
                        SELECT "TenantId", "AiProviderId"
                        FROM "ProviderOAuthTokens"
                        GROUP BY "TenantId", "AiProviderId"
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'ProviderOAuthTokens contains duplicate tenant/provider rows; repair before release';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_ProviderOAuthTokens_AiProviders_AiProviderId",
                table: "ProviderOAuthTokens");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_AiProviders_TenantId_Id",
                table: "AiProviders",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderOAuthTokens_TenantId_AiProviderId",
                table: "ProviderOAuthTokens",
                columns: new[] { "TenantId", "AiProviderId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ProviderOAuthTokens_AiProviders_TenantId_AiProviderId",
                table: "ProviderOAuthTokens",
                columns: new[] { "TenantId", "AiProviderId" },
                principalTable: "AiProviders",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProviderOAuthTokens_AiProviders_TenantId_AiProviderId",
                table: "ProviderOAuthTokens");

            migrationBuilder.DropIndex(
                name: "IX_ProviderOAuthTokens_TenantId_AiProviderId",
                table: "ProviderOAuthTokens");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AiProviders_TenantId_Id",
                table: "AiProviders");

            migrationBuilder.AddForeignKey(
                name: "FK_ProviderOAuthTokens_AiProviders_AiProviderId",
                table: "ProviderOAuthTokens",
                column: "AiProviderId",
                principalTable: "AiProviders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
