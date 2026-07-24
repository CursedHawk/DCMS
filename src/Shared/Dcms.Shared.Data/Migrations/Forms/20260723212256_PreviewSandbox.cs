using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Forms
{
    /// <inheritdoc />
    public partial class PreviewSandbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_form_submissions_TenantId_PluginInstanceId_FormName_Submitt~",
                schema: "forms",
                table: "form_submissions");

            migrationBuilder.AddColumn<bool>(
                name: "IsSandbox",
                schema: "forms",
                table: "form_submissions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_form_submissions_TenantId_IsSandbox_PluginInstanceId_FormNa~",
                schema: "forms",
                table: "form_submissions",
                columns: new[] { "TenantId", "IsSandbox", "PluginInstanceId", "FormName", "SubmittedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_form_submissions_TenantId_IsSandbox_PluginInstanceId_FormNa~",
                schema: "forms",
                table: "form_submissions");

            migrationBuilder.DropColumn(
                name: "IsSandbox",
                schema: "forms",
                table: "form_submissions");

            migrationBuilder.CreateIndex(
                name: "IX_form_submissions_TenantId_PluginInstanceId_FormName_Submitt~",
                schema: "forms",
                table: "form_submissions",
                columns: new[] { "TenantId", "PluginInstanceId", "FormName", "SubmittedAt" });
        }
    }
}
