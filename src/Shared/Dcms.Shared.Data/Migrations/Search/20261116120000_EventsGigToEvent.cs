using Dcms.Shared.Data.Search;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Search
{
    /// <summary>
    /// The search index follows the Events plugin's content type from <c>gig</c> to <c>event</c>
    /// (#320). Runs after the Cms migrations, which create <c>plugins.plugin_instances</c>.
    /// No model change, so hand-written and without a designer snapshot.
    /// </summary>
    [DbContext(typeof(SearchDbContext))]
    [Migration("20261116120000_EventsGigToEvent")]
    public partial class EventsGigToEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE search.search_documents d SET "ContentType" = 'event'
                FROM plugins.plugin_instances p
                WHERE d."PluginInstanceId" = p."Id" AND p."PluginId" = 'events' AND d."ContentType" = 'gig';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
