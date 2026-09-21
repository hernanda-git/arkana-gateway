using Microsoft.EntityFrameworkCore.Migrations;
using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace Arkana.Infrastructure.Migrations;

/// <summary>
/// Reconciles legacy dashboard-created native Gemini OAuth providers with the
/// ProviderAccounts control-plane projection required by ProviderTargetPlanner.
/// </summary>
[Migration("20260916153000_BackfillNativeGeminiProviderAccounts")]
[DbContext(typeof(GatewayDbContext))]
public partial class BackfillNativeGeminiProviderAccounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO "ProviderAccounts" (
                "Id", "TenantId", "AiProviderId", "Code", "DisplayName",
                "AuthOwnership", "BrokerKind", "BrokerCredentialId",
                "ExternalCredentialFileName", "BrokerInstanceId", "AuthDirectoryKey",
                "RoutingPrefix", "IsEnabled", "ConnectionStatus", "CooldownUntil",
                "LastSuccessAt", "LastFailureAt", "LastFailureClass", "LastBrokerSyncAt",
                "TokenExpiresAt", "Version", "CreatedAt", "UpdatedAt", "DeletedAt",
                "AuditActor", "SupportedModels")
            SELECT
                gen_random_uuid(),
                p."TenantId",
                p."Id",
                p."Code",
                p."Name",
                0,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                p."IsEnabled",
                CASE
                    WHEN t."Status" = 1 THEN 1
                    WHEN t."Status" IN (2, 3, 4) THEN 4
                    ELSE 0
                END,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                t."ExpiresAt",
                gen_random_uuid(),
                p."CreatedAt",
                now(),
                NULL,
                'migration:backfill-native-gemini-accounts',
                string_agg(m."Code", ',' ORDER BY m."Code")
            FROM "AiProviders" p
            LEFT JOIN "ProviderOAuthTokens" t
                ON t."AiProviderId" = p."Id"
                AND t."TenantId" = p."TenantId"
            LEFT JOIN "Models" m
                ON m."ProviderId" = p."Id"
            WHERE p."AuthMethod" = 1
              AND p."Code" LIKE 'gemini-acc%'
              AND NOT EXISTS (
                  SELECT 1
                  FROM "ProviderAccounts" existing
                  WHERE existing."TenantId" = p."TenantId"
                    AND existing."Code" = p."Code")
            GROUP BY
                p."TenantId", p."Id", p."Code", p."Name", p."IsEnabled",
                p."CreatedAt", t."Status", t."ExpiresAt"
            ON CONFLICT ("TenantId", "Code") DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM "ProviderAccounts"
            WHERE "AuditActor" = 'migration:backfill-native-gemini-accounts'
              AND "AuthOwnership" = 0
              AND "Code" LIKE 'gemini-acc%';
            """);
    }
}
