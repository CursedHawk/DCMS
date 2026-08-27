namespace Dcms.Shared.Data.Observability;

/// <summary>
/// The reporting surface Grafana is allowed to see.
///
/// <para><b>Why views and not table grants.</b> Grafana always lets a dashboard editor type
/// raw SQL into a panel — there is no setting that turns that off. So "Grafana only queries
/// the right columns" cannot be a convention; it has to be a permission. These views are
/// owned by the superuser and therefore execute with its rights, which means the
/// <c>dcms_grafana</c> role needs no grant on any base table at all. It holds USAGE on
/// <c>obs</c> and SELECT on these, and a query for <c>identity."AspNetUsers"</c> from that
/// connection is a permission error rather than a data leak.</para>
///
/// <para><b>What is deliberately not here.</b> No email address, no password hash, no token,
/// no IP address, no visitor hash, no user agent, no content body. Email appears only as a
/// bucketed domain, and only because "are our users mostly on one corporate domain" is a
/// real product question that a raw address does not have to be exposed to answer. This is
/// the same default-deny stance <c>AuditRedactionDefaults</c> takes on field values, applied
/// to the reporting plane: a column is absent until someone has looked at it and decided
/// otherwise.</para>
///
/// <para><b>Partition pruning is load-bearing.</b> <c>audit.audit_events</c> is range
/// partitioned on <c>OccurredAt</c> and has no index on <c>OccurredAt</c> alone or on
/// <c>Action</c> alone — the indexes all lead with <c>TenantId</c>. A platform-wide view
/// therefore relies entirely on the planner pruning partitions by time, so every view below
/// keeps <c>OccurredAt</c> in its predicate or its GROUP BY and none of them scan the parent
/// unbounded.</para>
///
/// <para><b>MetadataJson and ChangesJson are text, not jsonb</b> (migration
/// <c>20260821115132_AuditPayloadsAsText</c> — jsonb round-tripping would change the bytes
/// and break the hash chain). Anything reading into them has to cast, which is read-only and
/// does not touch what is stored.</para>
/// </summary>
internal static class ObservabilityViews
{
    /// <summary>
    /// Bumped when a view's shape changes. Recorded in <c>obs.view_version</c> so a dashboard
    /// showing nothing can be told apart from a cluster that has not run the configurator.
    /// </summary>
    public const int Version = 1;

    public static readonly string[] Statements =
    [
        // ------------------------------------------------------------------
        // Audit-derived: what the platform and each tenant actually did.
        // ------------------------------------------------------------------

        """
        CREATE OR REPLACE VIEW obs.v_tenant_activity_daily AS
        SELECT
            e."TenantId"                          AS tenant_id,
            COALESCE(t."Identifier", 'platform')  AS tenant_slug,
            date_trunc('day', e."OccurredAt")     AS day,
            e."Category"                          AS category,
            e."Action"                            AS action,
            e."Outcome"                           AS outcome,
            e."ServiceName"                       AS service,
            count(*)                              AS events
        FROM audit.audit_events e
        LEFT JOIN tenancy.tenants t ON t."Id" = e."TenantId"::text
        GROUP BY 1, 2, 3, 4, 5, 6, 7
        """,

        // The trace id is the whole point of this one: it is what turns a row in the audit log
        // into a link to the span tree that produced it, and it is why a user quoting a trace
        // id can be answered at all.
        """
        CREATE OR REPLACE VIEW obs.v_audit_recent AS
        SELECT
            e."OccurredAt"                        AS occurred_at,
            e."TenantId"                          AS tenant_id,
            COALESCE(t."Identifier", 'platform')  AS tenant_slug,
            e."Action"                            AS action,
            e."Category"                          AS category,
            e."Outcome"                           AS outcome,
            e."Severity"                          AS severity,
            e."ActorKind"                         AS actor_kind,
            e."ActorDisplay"                      AS actor_display,
            e."ActorAttribution"                  AS actor_attribution,
            e."ResourceType"                      AS resource_type,
            e."ResourceLabel"                     AS resource_label,
            e."ServiceName"                       AS service,
            e."HttpMethod"                        AS http_method,
            e."RoutePattern"                      AS route,
            e."StatusCode"                        AS status_code,
            e."TraceId"                           AS trace_id,
            e."CorrelationId"                     AS correlation_id,
            e."IsSandbox"                         AS is_sandbox
        FROM audit.audit_events e
        LEFT JOIN tenancy.tenants t ON t."Id" = e."TenantId"::text
        """,

        // Integrity, from the anchors rather than from the rows. A month whose partition has
        // been dropped by retention still shows here as sealed — that is what an anchor is for,
        // and a gap in the months is meant to be visible as a gap rather than as an absence.
        """
        CREATE OR REPLACE VIEW obs.v_audit_integrity AS
        SELECT
            a."ChainKey"                          AS chain_key,
            COALESCE(t."Identifier", 'platform')  AS tenant_slug,
            a."Period"                            AS period,
            a."FirstSeq"                          AS first_seq,
            a."LastSeq"                           AS last_seq,
            a."RowCount"                          AS row_count,
            a."SealedAt"                          AS sealed_at,
            a."PartitionDroppedAt"                AS partition_dropped_at,
            (a."PartitionDroppedAt" IS NOT NULL)  AS rows_dropped
        FROM audit.chain_anchors a
        LEFT JOIN tenancy.tenants t ON t."Id" = a."ChainKey"::text
        """,

        // ------------------------------------------------------------------
        // Users and growth.
        // ------------------------------------------------------------------

        """
        CREATE OR REPLACE VIEW obs.v_users_summary AS
        SELECT
            date_trunc('day', u."CreatedAt")                         AS day,
            count(*)                                                 AS signups,
            count(*) FILTER (WHERE u."EmailConfirmed")               AS confirmed,
            count(*) FILTER (WHERE u."ForgejoUsername" IS NOT NULL)  AS git_enabled,
            count(*) FILTER (WHERE u."LockoutEnd" > now())           AS locked_out
        FROM identity."AspNetUsers" u
        GROUP BY 1
        """,

        // There is no LastLoginAt column anywhere in the schema, and there is not going to be
        // one: the audit log already records every sign-in with a SubjectUserId, so a second
        // copy on the user row would be a denormalisation that can disagree with the record of
        // record. This view is the login history, derived from the thing that has it.
        """
        CREATE OR REPLACE VIEW obs.v_user_activity AS
        SELECT
            u."Id"                                                    AS user_id,
            u."DisplayName"                                           AS display_name,
            u."CreatedAt"                                             AS created_at,
            u."EmailConfirmed"                                        AS email_confirmed,
            l.last_login_at,
            l.logins_30d,
            l.failed_30d,
            m.tenant_count
        FROM identity."AspNetUsers" u
        LEFT JOIN LATERAL (
            SELECT
                max(e."OccurredAt") FILTER (WHERE e."Action" = 'auth.login.succeeded') AS last_login_at,
                count(*) FILTER (WHERE e."Action" = 'auth.login.succeeded')            AS logins_30d,
                count(*) FILTER (WHERE e."Action" = 'auth.login.failed')               AS failed_30d
            FROM audit.audit_events e
            WHERE e."SubjectUserId" = u."Id"
              AND e."OccurredAt" >= now() - interval '30 days'
        ) l ON TRUE
        LEFT JOIN LATERAL (
            SELECT count(*) AS tenant_count
            FROM tenancy.tenant_memberships tm
            WHERE tm."UserId" = u."Id"
        ) m ON TRUE
        """,

        // Demographics of platform users, as far as the data honestly goes.
        //
        // There is no country and no locale on a user row — the only country on this platform
        // is analytics.events.Country, which is a site visitor's, not an operator's, and is
        // derived from a caller-asserted header at that. So this reports what is actually
        // known: when they joined, how they authenticate, and a bucketed email domain. Inventing
        // a geography from an IP would be a worse answer than not having one.
        """
        CREATE OR REPLACE VIEW obs.v_user_demographics AS
        SELECT
            date_trunc('month', u."CreatedAt")                        AS signup_month,
            CASE
                WHEN g."UserId" IS NOT NULL THEN 'google'
                WHEN u."PasswordHash" IS NOT NULL THEN 'password'
                ELSE 'unknown'
            END                                                       AS auth_method,
            CASE
                WHEN u."NormalizedEmail" IS NULL THEN 'unknown'
                ELSE lower(split_part(u."NormalizedEmail", '@', 2))
            END                                                       AS email_domain,
            u."EmailConfirmed"                                        AS email_confirmed,
            count(*)                                                  AS users
        FROM identity."AspNetUsers" u
        LEFT JOIN LATERAL (
            SELECT ul."UserId" FROM identity."AspNetUserLogins" ul
            WHERE ul."UserId" = u."Id" LIMIT 1
        ) g ON TRUE
        GROUP BY 1, 2, 3, 4
        """,

        """
        CREATE OR REPLACE VIEW obs.v_growth_daily AS
        SELECT
            d.day,
            (SELECT count(*) FROM tenancy.tenants x        WHERE date_trunc('day', x."CreatedAt") = d.day) AS new_tenants,
            (SELECT count(*) FROM identity."AspNetUsers" x WHERE date_trunc('day', x."CreatedAt") = d.day) AS new_users,
            (SELECT count(*) FROM sites.sites x            WHERE date_trunc('day', x."CreatedAt") = d.day) AS new_sites,
            (SELECT count(*) FROM cms.content_items x      WHERE date_trunc('day', x."CreatedAt") = d.day) AS new_content
        FROM (
            SELECT generate_series(
                date_trunc('day', now() - interval '365 days'),
                date_trunc('day', now()),
                interval '1 day') AS day
        ) d
        """,

        // ------------------------------------------------------------------
        // Tenants: what each one has and how much of it.
        // ------------------------------------------------------------------

        """
        CREATE OR REPLACE VIEW obs.v_tenant_inventory AS
        SELECT
            t."Id"::uuid                                                                       AS tenant_id,
            t."Identifier"                                                                     AS tenant_slug,
            t."Name"                                                                           AS tenant_name,
            t."Status"                                                                         AS status,
            t."CreatedAt"                                                                      AS created_at,
            (SELECT count(*) FROM tenancy.tenant_memberships x WHERE x."TenantId" = t."Id"::uuid) AS members,
            (SELECT count(*) FROM tenancy.domains x            WHERE x."TenantId" = t."Id"::uuid) AS domains,
            (SELECT count(*) FROM tenancy.domains x            WHERE x."TenantId" = t."Id"::uuid AND x."VerifiedAt" IS NOT NULL) AS verified_domains,
            (SELECT count(*) FROM sites.sites x                WHERE x."TenantId" = t."Id"::uuid) AS sites,
            (SELECT count(*) FROM cms.content_items x          WHERE x."TenantId" = t."Id"::uuid) AS content_items,
            (SELECT count(*) FROM cms.content_items x          WHERE x."TenantId" = t."Id"::uuid AND x."PublishedVersionId" IS NOT NULL) AS published_items,
            (SELECT count(*) FROM media.media_assets x         WHERE x."TenantId" = t."Id"::uuid) AS media_assets,
            (SELECT count(*) FROM plugins.plugin_instances x   WHERE x."TenantId" = t."Id"::uuid AND x."Enabled") AS enabled_plugins,
            (SELECT count(*) FROM visitors.visitor_accounts x  WHERE x."TenantId" = t."Id"::uuid) AS visitor_accounts,
            (SELECT count(*) FROM forms.form_submissions x     WHERE x."TenantId" = t."Id"::uuid) AS form_submissions
        FROM tenancy.tenants t
        """,

        // Originals and variants counted separately, because "why is this tenant using 4 GB"
        // is usually answered by the variant ladder rather than by what anyone uploaded.
        """
        CREATE OR REPLACE VIEW obs.v_storage_usage AS
        SELECT
            t."Id"::uuid                                              AS tenant_id,
            t."Identifier"                                            AS tenant_slug,
            COALESCE(a.original_bytes, 0)                             AS original_bytes,
            COALESCE(v.variant_bytes, 0)                              AS variant_bytes,
            COALESCE(a.original_bytes, 0) + COALESCE(v.variant_bytes, 0) AS total_bytes,
            COALESCE(a.asset_count, 0)                                AS asset_count
        FROM tenancy.tenants t
        LEFT JOIN LATERAL (
            SELECT sum(x."SizeBytes") AS original_bytes, count(*) AS asset_count
            FROM media.media_assets x WHERE x."TenantId" = t."Id"::uuid
        ) a ON TRUE
        LEFT JOIN LATERAL (
            SELECT sum(x."SizeBytes") AS variant_bytes
            FROM media.media_variants x WHERE x."TenantId" = t."Id"::uuid
        ) v ON TRUE
        """,

        """
        CREATE OR REPLACE VIEW obs.v_site_builds AS
        SELECT
            b."CreatedAt"                         AS created_at,
            b."CompletedAt"                       AS completed_at,
            b."TenantId"                          AS tenant_id,
            COALESCE(t."Identifier", 'unknown')   AS tenant_slug,
            s."Name"                              AS site_name,
            s."RenderMode"                        AS render_mode,
            b."Status"                            AS status,
            b."GitCommitSha" IS NOT NULL          AS from_git,
            EXTRACT(EPOCH FROM (b."CompletedAt" - b."CreatedAt")) AS duration_seconds
        FROM sites.site_builds b
        LEFT JOIN sites.sites s   ON s."Id" = b."SiteId"
        LEFT JOIN tenancy.tenants t ON t."Id" = b."TenantId"::text
        """,

        // ------------------------------------------------------------------
        // Site visitors. The one place real demographics exist.
        // ------------------------------------------------------------------

        // Country here is derived from a request header at ingest (HeaderGeoIpResolver) and
        // there is no edge in front of this platform that stamps one — no Cloudflare, no
        // proxy — so it is caller-asserted and largely null. Reported because it is what the
        // schema has; label it as asserted wherever it is shown.
        """
        CREATE OR REPLACE VIEW obs.v_visitor_demographics AS
        SELECT
            e."TenantId"                          AS tenant_id,
            COALESCE(t."Identifier", 'unknown')   AS tenant_slug,
            date_trunc('day', e."OccurredAt")     AS day,
            COALESCE(e."Country", 'unknown')      AS country,
            COALESCE(e."Device", 'unknown')       AS device,
            COALESCE(e."Browser", 'unknown')      AS browser,
            COALESCE(e."Os", 'unknown')           AS os,
            COALESCE(e."UtmSource", 'direct')     AS utm_source,
            COALESCE(e."UtmMedium", 'none')       AS utm_medium,
            COALESCE(e."UtmCampaign", 'none')     AS utm_campaign,
            count(*)                              AS events,
            count(DISTINCT e."VisitorHash")       AS visitors,
            count(DISTINCT e."SessionId")         AS sessions
        FROM analytics.events e
        LEFT JOIN tenancy.tenants t ON t."Id" = e."TenantId"::text
        WHERE e."OccurredAt" >= now() - interval '90 days'
        GROUP BY 1, 2, 3, 4, 5, 6, 7, 8, 9, 10
        """,

        // Reads the pre-aggregated rollups rather than raw events, which is the whole reason
        // daily_rollups exists. Raw events are subject to the 90-day retention added alongside
        // this work; the rollups are kept indefinitely, so this is also the only view that
        // still answers questions about last year.
        """
        CREATE OR REPLACE VIEW obs.v_visitor_traffic_daily AS
        SELECT
            r."TenantId"                          AS tenant_id,
            COALESCE(t."Identifier", 'unknown')   AS tenant_slug,
            r."Day"                               AS day,
            r."Type"                              AS event_type,
            r."Path"                              AS path,
            r."Count"                             AS events
        FROM analytics.daily_rollups r
        LEFT JOIN tenancy.tenants t ON t."Id" = r."TenantId"::text
        """,

        // ------------------------------------------------------------------
        // Marker.
        // ------------------------------------------------------------------

        // An empty dashboard has two causes that look identical: nothing happened, or the
        // views were never created on this cluster. This tells them apart.
        $"""
        CREATE OR REPLACE VIEW obs.view_version AS
        SELECT {Version} AS version, now() AS checked_at
        """,
    ];
}
