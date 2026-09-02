using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Social
{
    /// <inheritdoc />
    public partial class InitialSocial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "social");

            migrationBuilder.CreateTable(
                name: "meta_connections",
                schema: "social",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExternalAccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExternalPageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AccountName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AccountUsername = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AvatarUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    AccessTokenCiphertext = table.Column<string>(type: "text", nullable: false),
                    PageTokenCiphertext = table.Column<string>(type: "text", nullable: true),
                    TokenExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ScopesGranted = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ConnectedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastRefreshedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meta_connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "meta_media_map",
                schema: "social",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalMediaId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MediaAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    MirroredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meta_media_map", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "meta_oauth_states",
                schema: "social",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StateHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PluginInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    InitiatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meta_oauth_states", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "meta_sync_states",
                schema: "social",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PluginInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastCursor = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meta_sync_states", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_meta_connections_TenantId_Provider_ExternalAccountId",
                schema: "social",
                table: "meta_connections",
                columns: new[] { "TenantId", "Provider", "ExternalAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_meta_media_map_TenantId_ConnectionId_ExternalMediaId",
                schema: "social",
                table: "meta_media_map",
                columns: new[] { "TenantId", "ConnectionId", "ExternalMediaId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_meta_media_map_TenantId_MediaAssetId",
                schema: "social",
                table: "meta_media_map",
                columns: new[] { "TenantId", "MediaAssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_meta_oauth_states_ExpiresAt",
                schema: "social",
                table: "meta_oauth_states",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_meta_oauth_states_StateHash",
                schema: "social",
                table: "meta_oauth_states",
                column: "StateHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_meta_sync_states_NextAttemptAt",
                schema: "social",
                table: "meta_sync_states",
                column: "NextAttemptAt");

            migrationBuilder.CreateIndex(
                name: "IX_meta_sync_states_TenantId_PluginInstanceId_ContentType",
                schema: "social",
                table: "meta_sync_states",
                columns: new[] { "TenantId", "PluginInstanceId", "ContentType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meta_connections",
                schema: "social");

            migrationBuilder.DropTable(
                name: "meta_media_map",
                schema: "social");

            migrationBuilder.DropTable(
                name: "meta_oauth_states",
                schema: "social");

            migrationBuilder.DropTable(
                name: "meta_sync_states",
                schema: "social");
        }
    }
}
