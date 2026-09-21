using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations;

/// <summary>
/// Removes the operation-version column emitted by the earlier migration under
/// a name that is not part of the runtime model. IF EXISTS keeps this corrective
/// migration safe for databases that stopped before the orphaned operation ran.
/// </summary>
public partial class RemoveOrphanedExpectedAccountVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "ALTER TABLE \"ProviderAccountOperations\" " +
            "DROP COLUMN IF EXISTS \"ExpectedAccountVersion\";");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "ExpectedAccountVersion",
            table: "ProviderAccountOperations",
            type: "uuid",
            nullable: false,
            defaultValue: Guid.Empty);
    }
}
