using Dcms.Shared.Data.Cms;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Cms
{
    /// <summary>
    /// The Events plugin's content type is <c>event</c>, no longer <c>gig</c>, and its page size is
    /// <c>eventsPerPage</c> (#320). No model change, so hand-written and without a designer snapshot.
    /// </summary>
    [DbContext(typeof(CmsDbContext))]
    [Migration("20261116120000_EventsGigToEvent")]
    public partial class EventsGigToEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE cms.content_items i SET "ContentType" = 'event'
                FROM plugins.plugin_instances p
                WHERE i."PluginInstanceId" = p."Id" AND p."PluginId" = 'events' AND i."ContentType" = 'gig';

                UPDATE plugins.plugin_instances
                SET "ConfigJson" = ("ConfigJson" - 'gigsPerPage') || jsonb_build_object('eventsPerPage', "ConfigJson" -> 'gigsPerPage')
                WHERE "PluginId" = 'events' AND "ConfigJson" ? 'gigsPerPage';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
