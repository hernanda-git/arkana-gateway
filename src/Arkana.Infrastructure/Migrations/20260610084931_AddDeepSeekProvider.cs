using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Arkana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeepSeekProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DeepSeek models are served through the OpenCode provider
            // (deepseek-v4-flash / deepseek-v4-pro). The standalone
            // "DeepSeek Direct" provider was removed to avoid model-code
            // ambiguity ("deepseek-v4-flash" vs "deepseek-v4-flash-direct").
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "AiProviders",
                columns: new[] { "Id", "ApiKey", "BaseUrl", "Code", "CostPerInputToken", "CostPerOutputToken", "CreatedAt", "IsEnabled", "MaxTokensPerRequest", "Name", "Priority" },
                values: new object[] { new Guid("a1000000-0000-0000-0000-000000000006"), null, "https://api.deepseek.com/v1", "deepseek", 0.00000014m, 0.00000028m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, null, "DeepSeek Direct", 0 });

            migrationBuilder.InsertData(
                table: "Models",
                columns: new[] { "Id", "Code", "CostPerInputToken", "CostPerOutputToken", "CreatedAt", "IsEnabled", "MaxTokensPerRequest", "Name", "ProviderId" },
                values: new object[,]
                {
                    { new Guid("b0000000-0000-0000-0000-000000000051"), "deepseek-v4-flash-direct", 0.00000014m, 0.00000028m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, null, "DeepSeek V4 Flash (Direct)", new Guid("a1000000-0000-0000-0000-000000000006") },
                    { new Guid("b0000000-0000-0000-0000-000000000052"), "deepseek-v4-pro-direct", 0.00000174m, 0.00000348m, new DateTimeOffset(new DateTime(2026, 6, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, null, "DeepSeek V4 Pro (Direct)", new Guid("a1000000-0000-0000-0000-000000000006") }
                });
        }
    }
}
