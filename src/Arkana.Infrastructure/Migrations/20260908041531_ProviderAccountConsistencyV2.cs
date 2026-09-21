using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProviderAccountConsistencyV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BrokerInstanceId",
                table: "ProviderAccountOperations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BrokerKind",
                table: "ProviderAccountOperations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataJson",
                table: "ProviderAccountOperations",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StableCredentialId",
                table: "ProviderAccountOperations",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BrokerInstanceId",
                table: "ProviderAccountOperations");

            migrationBuilder.DropColumn(
                name: "BrokerKind",
                table: "ProviderAccountOperations");

            migrationBuilder.DropColumn(
                name: "MetadataJson",
                table: "ProviderAccountOperations");

            migrationBuilder.DropColumn(
                name: "StableCredentialId",
                table: "ProviderAccountOperations");
        }
    }
}
