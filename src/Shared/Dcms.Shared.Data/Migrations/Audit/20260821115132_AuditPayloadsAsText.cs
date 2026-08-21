using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Audit
{
    /// <summary>
    /// Moves the two hashed payload columns from <c>jsonb</c> to <c>text</c>.
    ///
    /// <para>jsonb is a parsed representation, not a string: Postgres reorders object keys by
    /// length, rewrites separators (<c>": "</c> rather than <c>":"</c>) and normalises numbers.
    /// Both columns are covered by the chain hash, which is computed over the text the writer
    /// produced — so the value read back never matched the value hashed, and every record with a
    /// non-empty payload failed verification with "this row was altered after it was written".
    /// A storage detail was raising a tampering alarm.</para>
    ///
    /// <para>Nothing queries inside these payloads — there is deliberately no GIN index — and the
    /// read plane hands the stored text to the browser verbatim. jsonb was buying nothing and
    /// costing the integrity guarantee outright.</para>
    ///
    /// <para><b>Rows written before this migration stay unverifiable.</b> The conversion stores
    /// Postgres's normalised rendering, which is not what those rows were hashed over, and the
    /// original bytes no longer exist anywhere. They carry <c>HashVersion = 1</c> and the verifier
    /// reports them as hashed under an older algorithm rather than as altered.</para>
    /// </summary>
    public partial class AuditPayloadsAsText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-written: audit_events is mapped ExcludeFromMigrations because its CREATE TABLE
            // is partitioned DDL that MigrationBuilder cannot express. ALTER on the partitioned
            // parent cascades to every partition.
            migrationBuilder.Sql("""
                ALTER TABLE audit.audit_events
                    ALTER COLUMN "MetadataJson" TYPE text USING "MetadataJson"::text,
                    ALTER COLUMN "ChangesJson"  TYPE text USING "ChangesJson"::text;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE audit.audit_events
                    ALTER COLUMN "MetadataJson" TYPE jsonb USING "MetadataJson"::jsonb,
                    ALTER COLUMN "ChangesJson"  TYPE jsonb USING "ChangesJson"::jsonb;
                """);
        }
    }
}
