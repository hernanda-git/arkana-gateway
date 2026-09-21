using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestLogViaMitmAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This migration removes the per-provider/per-model tenancy columns that earlier
            // migrations added on the original database lineage. A database created from scratch
            // never gets them, so the generated unconditional DROP statements (constraint, index,
            // column) abort the whole migration run — and with it gateway startup. The operations
            // are therefore expressed as IF EXISTS: identical end state on both lineages.
            migrationBuilder.Sql(
                "ALTER TABLE \"AiProviders\" DROP CONSTRAINT IF EXISTS \"FK_AiProviders_Tenants_TenantId\";");

            migrationBuilder.Sql(
                "ALTER TABLE \"Models\" DROP CONSTRAINT IF EXISTS \"FK_Models_Tenants_TenantId\";");

            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_Models_TenantId\";");

            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_AiProviders_TenantId\";");

            migrationBuilder.Sql(
                "ALTER TABLE \"Models\" DROP COLUMN IF EXISTS \"TenantId\";");

            migrationBuilder.Sql(
                "ALTER TABLE \"AiProviders\" DROP COLUMN IF EXISTS \"TenantId\";");

            migrationBuilder.Sql(
                "ALTER TABLE \"RequestLogs\" ADD COLUMN IF NOT EXISTS \"ViaMitmAgent\" text;");

            migrationBuilder.Sql(
                "ALTER TABLE \"DashboardUsers\" ADD COLUMN IF NOT EXISTS \"Email\" character varying(256);");

            migrationBuilder.Sql(
                "ALTER TABLE \"DashboardUsers\" ADD COLUMN IF NOT EXISTS \"LastLoginAt\" timestamp with time zone;");

            migrationBuilder.Sql(
                "ALTER TABLE \"DashboardUsers\" ADD COLUMN IF NOT EXISTS \"LoginCount\" integer NOT NULL DEFAULT 0;");

            migrationBuilder.CreateTable(
                name: "AgentDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    SystemPrompt = table.Column<string>(type: "text", nullable: false),
                    ModelCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MaxTokens = table.Column<int>(type: "integer", nullable: false),
                    Temperature = table.Column<decimal>(type: "numeric", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Metadata = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeyPools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AiProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActiveIndex = table.Column<int>(type: "integer", nullable: false),
                    TotalRequests = table.Column<long>(type: "bigint", nullable: false),
                    TotalRateLimits = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeyPools", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeyPools_AiProviders_AiProviderId",
                        column: x => x.AiProviderId,
                        principalTable: "AiProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApiKeyPools_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // PolicyTemplates (table + indexes) is created identically by an earlier migration on the
            // current lineage, so the table is created only if absent; this migration still owns the
            // built-in rows inserted below.
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "PolicyTemplates" (
                    "Id" uuid NOT NULL,
                    "Name" character varying(128) NOT NULL,
                    "Slug" character varying(128) NOT NULL,
                    "Description" character varying(1024) NOT NULL,
                    "Config" text NOT NULL,
                    "IsActive" boolean NOT NULL,
                    "IsBuiltin" boolean NOT NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_PolicyTemplates" PRIMARY KEY ("Id")
                );
                """);

            migrationBuilder.CreateTable(
                name: "SlaMetrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ModelCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AvgLatencyMs = table.Column<double>(type: "double precision", nullable: false),
                    P50LatencyMs = table.Column<double>(type: "double precision", nullable: false),
                    P95LatencyMs = table.Column<double>(type: "double precision", nullable: false),
                    P99LatencyMs = table.Column<double>(type: "double precision", nullable: false),
                    MaxLatencyMs = table.Column<double>(type: "double precision", nullable: false),
                    TotalRequests = table.Column<long>(type: "bigint", nullable: false),
                    SuccessfulRequests = table.Column<long>(type: "bigint", nullable: false),
                    FailedRequests = table.Column<long>(type: "bigint", nullable: false),
                    ErrorRate = table.Column<double>(type: "double precision", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    LastFailureAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UptimePercent = table.Column<double>(type: "double precision", nullable: false),
                    IsHealthy = table.Column<bool>(type: "boolean", nullable: false),
                    LastHealthCheckAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WindowStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WindowEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaMetrics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlaMetrics_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Webhooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Secret = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Events = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 3),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastTriggeredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Webhooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Webhooks_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Input = table.Column<string>(type: "text", nullable: false),
                    Output = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    TokenUsed = table.Column<int>(type: "integer", nullable: true),
                    ParentTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentTasks_AgentDefinitions_AgentId",
                        column: x => x.AgentId,
                        principalTable: "AgentDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeyPoolEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PoolId = table.Column<Guid>(type: "uuid", nullable: false),
                    SealedKey = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    RateLimitedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CooldownSeconds = table.Column<int>(type: "integer", nullable: false),
                    ConsecutiveRateLimits = table.Column<int>(type: "integer", nullable: false),
                    RequestCount = table.Column<long>(type: "bigint", nullable: false),
                    RateLimitCount = table.Column<long>(type: "bigint", nullable: false),
                    IsPermanentlyDisabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastErrorType = table.Column<string>(type: "text", nullable: true),
                    AllowedModels = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeyPoolEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeyPoolEntries_ApiKeyPools_PoolId",
                        column: x => x.PoolId,
                        principalTable: "ApiKeyPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "PolicyTemplates",
                columns: new[] { "Id", "Config", "CreatedAt", "Description", "IsActive", "IsBuiltin", "Name", "Slug" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), "{\"pii_detection\":{\"enabled\":true,\"patterns\":[\"email\",\"phone\",\"name\",\"address\",\"ip\"],\"engine\":\"internal_only\"},\"data_retention\":{\"audit_days\":365,\"log_level\":\"full\"},\"external_engines\":false,\"masking\":\"full\",\"consent_required\":true}", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "EU General Data Protection Regulation — strict PII detection, no external engines, 365-day audit retention.", true, true, "GDPR", "gdpr" },
                    { new Guid("10000000-0000-0000-0000-000000000002"), "{\"pii_detection\":{\"enabled\":true,\"patterns\":[\"email\",\"phone\",\"name\",\"address\",\"ssn\",\"credit_card\",\"ip\",\"date_of_birth\"],\"engine\":\"all\"},\"masking\":\"full\",\"external_engines\":true,\"consent_required\":false,\"content_filter\":{\"toxicity_threshold\":0.7}}", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Aggressive PII masking across all engines — detects and masks all personally identifiable information.", true, true, "PII Strict", "pii-strict" },
                    { new Guid("10000000-0000-0000-0000-000000000003"), "{\"pii_detection\":{\"enabled\":false},\"masking\":\"none\",\"audit\":{\"enabled\":true,\"log_requests\":true,\"log_responses\":true,\"log_metadata\":true,\"retention_days\":730},\"external_engines\":true,\"content_filter\":null}", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Log-only mode with full audit trail — tracks all requests without masking, for compliance review.", true, true, "Compliance Audit", "compliance-audit" },
                    { new Guid("10000000-0000-0000-0000-000000000004"), "{\"pii_detection\":{\"enabled\":true,\"patterns\":[\"email\",\"phone\",\"credit_card\"],\"engine\":\"internal_only\"},\"masking\":\"partial\",\"topic_allow_list\":[\"general\",\"support\",\"faq\"],\"content_filter\":{\"toxicity_threshold\":0.9,\"block_profanity\":true},\"max_tokens_per_request\":4096}", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Minimal guardrails for public-facing chatbots — topic allow-list, basic content filtering.", true, true, "Public Chatbot", "public-chatbot" },
                    { new Guid("10000000-0000-0000-0000-000000000005"), "{\"pii_detection\":{\"enabled\":true,\"patterns\":[\"nik\",\"npwp\",\"email\",\"phone\",\"name\",\"address\"],\"custom_patterns\":[{\"name\":\"nik\",\"regex\":\"\\\\b\\\\d{16}\\\\b\",\"description\":\"Nomor Induk Kependudukan\"},{\"name\":\"npwp\",\"regex\":\"\\\\b\\\\d{2}\\\\.\\\\d{3}\\\\.\\\\d{3}\\\\.\\\\d{1}-\\\\d{3}\\\\.\\\\d{3}\\\\b\",\"description\":\"Nomor Pokok Wajib Pajak\"}],\"engine\":\"internal_only\"},\"masking\":\"full\",\"external_engines\":false,\"localization\":\"id-ID\"}", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Indonesian-specific PII detection — NIK, NPWP, and national identity patterns with mandatory masking.", true, true, "Indonesia PII", "indonesia-pii" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitions_TenantId",
                table: "AgentDefinitions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTasks_AgentId",
                table: "AgentTasks",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTasks_Status",
                table: "AgentTasks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTasks_TenantId",
                table: "AgentTasks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyPoolEntries_PoolId",
                table: "ApiKeyPoolEntries",
                column: "PoolId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyPools_AiProviderId_TenantId",
                table: "ApiKeyPools",
                columns: new[] { "AiProviderId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyPools_TenantId",
                table: "ApiKeyPools",
                column: "TenantId");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_PolicyTemplates_IsActive\" ON \"PolicyTemplates\" (\"IsActive\");");

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_PolicyTemplates_Slug\" ON \"PolicyTemplates\" (\"Slug\");");

            migrationBuilder.CreateIndex(
                name: "IX_SlaMetrics_ProviderCode_ModelCode_TenantId",
                table: "SlaMetrics",
                columns: new[] { "ProviderCode", "ModelCode", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlaMetrics_TenantId",
                table: "SlaMetrics",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Webhooks_TenantId",
                table: "Webhooks",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentTasks");

            migrationBuilder.DropTable(
                name: "ApiKeyPoolEntries");

            migrationBuilder.DropTable(
                name: "PolicyTemplates");

            migrationBuilder.DropTable(
                name: "SlaMetrics");

            migrationBuilder.DropTable(
                name: "Webhooks");

            migrationBuilder.DropTable(
                name: "AgentDefinitions");

            migrationBuilder.DropTable(
                name: "ApiKeyPools");

            migrationBuilder.DropColumn(
                name: "ViaMitmAgent",
                table: "RequestLogs");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "DashboardUsers");

            migrationBuilder.DropColumn(
                name: "LastLoginAt",
                table: "DashboardUsers");

            migrationBuilder.DropColumn(
                name: "LoginCount",
                table: "DashboardUsers");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Models",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AiProviders",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000001"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000002"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000003"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000004"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000005"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "AiProviders",
                keyColumn: "Id",
                keyValue: new Guid("a1000000-0000-0000-0000-000000000006"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000001"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000002"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000003"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000004"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000005"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000006"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000007"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000008"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000011"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000012"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000013"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000014"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000015"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000021"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000022"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000023"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000031"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000032"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000033"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000041"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000042"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000043"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000044"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000051"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "Models",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000052"),
                column: "TenantId",
                value: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.CreateIndex(
                name: "IX_Models_TenantId",
                table: "Models",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AiProviders_TenantId",
                table: "AiProviders",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_AiProviders_Tenants_TenantId",
                table: "AiProviders",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Models_Tenants_TenantId",
                table: "Models",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
