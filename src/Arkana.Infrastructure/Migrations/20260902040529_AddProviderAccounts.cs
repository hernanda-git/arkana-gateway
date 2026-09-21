using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccountRoutingMode",
                table: "ApiKeys",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "AllowAccountFallback",
                table: "ApiKeys",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "PreferredProviderAccountId",
                table: "ApiKeys",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProviderAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AiProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AuthOwnership = table.Column<int>(type: "integer", nullable: false),
                    BrokerKind = table.Column<int>(type: "integer", nullable: true),
                    BrokerCredentialId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ExternalCredentialFileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    BrokerInstanceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AuthDirectoryKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RoutingPrefix = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ConnectionStatus = table.Column<int>(type: "integer", nullable: false),
                    CooldownUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFailureAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFailureClass = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastBrokerSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TokenExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AuditActor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderAccounts_AiProviders_AiProviderId",
                        column: x => x.AiProviderId,
                        principalTable: "AiProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProviderAccounts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_PreferredProviderAccountId",
                table: "ApiKeys",
                column: "PreferredProviderAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAccounts_AiProviderId",
                table: "ProviderAccounts",
                column: "AiProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAccounts_TenantId_AiProviderId_IsEnabled_Connection~",
                table: "ProviderAccounts",
                columns: new[] { "TenantId", "AiProviderId", "IsEnabled", "ConnectionStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAccounts_TenantId_BrokerInstanceId",
                table: "ProviderAccounts",
                columns: new[] { "TenantId", "BrokerInstanceId" },
                unique: true,
                filter: "\"BrokerInstanceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAccounts_TenantId_Code",
                table: "ProviderAccounts",
                columns: new[] { "TenantId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderAccounts");

            migrationBuilder.DropIndex(
                name: "IX_ApiKeys_PreferredProviderAccountId",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "AccountRoutingMode",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "AllowAccountFallback",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "PreferredProviderAccountId",
                table: "ApiKeys");
        }
    }
}
