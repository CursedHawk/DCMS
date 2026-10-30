using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Sites
{
    /// <inheritdoc />
    public partial class RepoBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "repo_bindings",
                schema: "sites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PluginId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepoFullName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DefaultBranch = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ReadPermission = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EditPermission = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PublishPermission = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repo_bindings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_repo_bindings_RepoFullName",
                schema: "sites",
                table: "repo_bindings",
                column: "RepoFullName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_repo_bindings_TenantId_EnvironmentId_InstanceId",
                schema: "sites",
                table: "repo_bindings",
                columns: new[] { "TenantId", "EnvironmentId", "InstanceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "repo_bindings",
                schema: "sites");
        }
    }
}
