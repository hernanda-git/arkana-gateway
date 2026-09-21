using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkana.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SyncSnapshot : Migration
{
    /// <inheritdoc />
    /// <remarks>
    /// No-op. This migration exists only to lock in the corrected model snapshot
    /// produced by the 2026-08-06 migration consolidation (see
    /// docs/code-review-2026-08-06-full-audit.md, finding C2). The prior snapshot
    /// lived in a separate folder and had drifted: it omitted 3 OAuth tables and
    /// 4 columns that shipped in later migrations. On the deployed database those
    /// objects already exist, so a real Up() would fail with "already exists". A
    /// fresh database gets them from the earlier migrations in this folder; an
    /// existing database already has them. Either way Up() does nothing.
    /// </remarks>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
