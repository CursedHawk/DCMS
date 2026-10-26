using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Forms
{
    /// <inheritdoc />
    public partial class DropPreviewSandbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The preview sandbox is gone (ADR 0026): its rows are test data nobody can reach any
            // more, and a visitor's sandbox account would collide with their live one on the new
            // unique (TenantId, Email) index.
            migrationBuilder.Sql("""DELETE FROM forms.form_submissions WHERE "IsSandbox";""");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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
    }
}
