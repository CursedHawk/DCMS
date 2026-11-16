using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Ai
{
    /// <inheritdoc />
    public partial class AddWorkspaceProvidersAndUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProviderId",
                schema: "ai",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "providers",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    BaseUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ApiKeyCiphertext = table.Column<string>(type: "text", nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_providers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "usage",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ProviderKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Calls = table.Column<long>(type: "bigint", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheWriteTokens = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_providers_TenantId_Name",
                schema: "ai",
                table: "providers",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_providers_TenantId_default",
                schema: "ai",
                table: "providers",
                column: "TenantId",
                unique: true,
                filter: "\"IsDefault\"");

            migrationBuilder.CreateIndex(
                name: "IX_usage_key",
                schema: "ai",
                table: "usage",
                columns: new[] { "TenantId", "Day", "Source", "ProviderId", "UserId", "Model" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            // The provider a workspace configured becomes its first, default provider: same kind,
            // model, URL and (still Transit-encrypted) key. A workspace that configured nothing
            // keeps the platform default by having no provider at all.
            migrationBuilder.Sql("""
                INSERT INTO ai.providers ("Id", "TenantId", "Name", "Provider", "Model", "BaseUrl", "ApiKeyCiphertext", "IsDefault", "UpdatedBy", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), "TenantId",
                       CASE "Provider" WHEN 'Anthropic' THEN 'Anthropic' WHEN 'OpenAi' THEN 'OpenAI' WHEN 'Ollama' THEN 'Ollama'
                                       WHEN 'LmStudio' THEN 'LM Studio' ELSE 'Platform provider' END,
                       "Provider", NULLIF(btrim("Model"), ''), NULLIF(btrim("BaseUrl"), ''), NULLIF("ApiKeyCiphertext", ''),
                       true, "UpdatedBy", "UpdatedAt", "UpdatedAt"
                FROM ai.tenant_ai_settings
                WHERE "Provider" <> 'Inherit' OR coalesce("ApiKeyCiphertext", '') <> ''
                   OR coalesce(btrim("Model"), '') <> '' OR coalesce(btrim("BaseUrl"), '') <> '';
                """);

            migrationBuilder.DropColumn(
                name: "ApiKeyCiphertext",
                schema: "ai",
                table: "tenant_ai_settings");

            migrationBuilder.DropColumn(
                name: "BaseUrl",
                schema: "ai",
                table: "tenant_ai_settings");

            migrationBuilder.DropColumn(
                name: "Model",
                schema: "ai",
                table: "tenant_ai_settings");

            migrationBuilder.DropColumn(
                name: "Provider",
                schema: "ai",
                table: "tenant_ai_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "providers",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "usage",
                schema: "ai");

            migrationBuilder.DropColumn(
                name: "ProviderId",
                schema: "ai",
                table: "conversations");

            migrationBuilder.AddColumn<string>(
                name: "ApiKeyCiphertext",
                schema: "ai",
                table: "tenant_ai_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BaseUrl",
                schema: "ai",
                table: "tenant_ai_settings",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                schema: "ai",
                table: "tenant_ai_settings",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                schema: "ai",
                table: "tenant_ai_settings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");
        }
    }
}
