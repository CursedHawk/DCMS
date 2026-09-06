using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Edge
{
    /// <inheritdoc />
    public partial class EdgeManagedCertificates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ManagedCertificateId",
                schema: "edge",
                table: "certificates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "SubjectAlternativeNames",
                schema: "edge",
                table: "certificates",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.CreateTable(
                name: "managed_certificates",
                schema: "edge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Identifiers = table.Column<string[]>(type: "text[]", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_certificates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "managed_certificate_attempts",
                schema: "edge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ManagedCertificateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Identifiers = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AttemptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_certificate_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_managed_certificate_attempts_managed_certificates_ManagedCe~",
                        column: x => x.ManagedCertificateId,
                        principalSchema: "edge",
                        principalTable: "managed_certificates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_certificates_ManagedCertificateId",
                schema: "edge",
                table: "certificates",
                column: "ManagedCertificateId",
                unique: true,
                filter: "\"ManagedCertificateId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_certificates_SubjectAlternativeNames",
                schema: "edge",
                table: "certificates",
                column: "SubjectAlternativeNames")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_managed_certificate_attempts_ManagedCertificateId_Attempted~",
                schema: "edge",
                table: "managed_certificate_attempts",
                columns: new[] { "ManagedCertificateId", "AttemptedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "managed_certificate_attempts",
                schema: "edge");

            migrationBuilder.DropTable(
                name: "managed_certificates",
                schema: "edge");

            migrationBuilder.DropIndex(
                name: "IX_certificates_ManagedCertificateId",
                schema: "edge",
                table: "certificates");

            migrationBuilder.DropIndex(
                name: "IX_certificates_SubjectAlternativeNames",
                schema: "edge",
                table: "certificates");

            migrationBuilder.DropColumn(
                name: "ManagedCertificateId",
                schema: "edge",
                table: "certificates");

            migrationBuilder.DropColumn(
                name: "SubjectAlternativeNames",
                schema: "edge",
                table: "certificates");
        }
    }
}
