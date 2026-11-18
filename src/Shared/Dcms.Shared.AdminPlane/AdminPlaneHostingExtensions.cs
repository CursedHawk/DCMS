using Dcms.AdminApi.Notifications;
using Dcms.AdminApi.Tenancy;
using Dcms.AdminApi.Tenancy.Usage;
using Dcms.AdminApi.Media;
using Dcms.Plugins.All;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Hosting;
using Dcms.Shared.Caching;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Analytics;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.DataProtection;
using Dcms.Shared.Data.DynamicApps;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Functions;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Notifications;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Search;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Social;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.UserAuth;
using Dcms.Shared.Data.Visitors;
using Dcms.Shared.Hosting;
using Dcms.Shared.Jobs;
using Dcms.Shared.Messaging;
using Dcms.Shared.Messaging.Email;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Dcms.Shared.Security.Realms;
using Dcms.Shared.Storage;
using Dcms.Shared.Vault;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dcms.Shared.AdminPlane;

/// <summary>
/// The admin plane's composition, called by both of its services (ADR 0036): admin-api, which
/// serves the console, and jobs-worker, which does the background work. One method so the two can
/// never drift -- they share a database, a message bus, a SignalR backplane, plugins and the
/// domain services both reach for -- while what only one of them does (endpoints, middleware and
/// sockets; hosted services and job handlers) is registered by that service alone.
/// </summary>
public static class AdminPlaneHostingExtensions
{
    /// <param name="serviceName">Who is running: names the job ledger's writer.</param>
    /// <param name="runsBackgroundWork">
    /// Whether plugins register their hosted services here: true in jobs-worker only.
    /// </param>
    public static WebApplicationBuilder AddDcmsAdminPlane(this WebApplicationBuilder builder, string serviceName, bool runsBackgroundWork)
    {
        builder.Services.AddDcmsMessaging(builder.Configuration);
        builder.Services.AddDcmsCaching(builder.Configuration);
        builder.Services.AddDcmsEmailQueue();

        // Notification fan-out across replicas. The channel prefix MUST differ from content-api's
        // "dcms-chat": both services share one Redis, and a shared prefix would cross-deliver
        // between the two hubs.
        var notificationSignalR = builder.Services.AddSignalR();
        var signalRRedis = builder.Configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(signalRRedis))
        {
            notificationSignalR.AddStackExchangeRedis(signalRRedis + ",abortConnect=false",
                options => options.Configuration.ChannelPrefix =
                    StackExchange.Redis.RedisChannel.Literal("dcms-notify"));
        }

        // Tenancy: shared TenancyDbContext + header-based tenant resolution.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddDcmsObjectStorage(builder.Configuration);
        builder.Services.AddDcmsTenancyData(builder.Configuration);
        builder.Services.AddDcmsCmsData(builder.Configuration);
        builder.Services.AddDcmsMediaData(builder.Configuration);
        builder.Services.AddDcmsSitesData(builder.Configuration);
        builder.Services.AddDcmsAiData(builder.Configuration);
        builder.Services.AddDcmsSocialData(builder.Configuration);
        // Shared by the upload endpoint and the Meta feed sync -- see MediaIngestService.
        builder.Services.AddScoped<Dcms.AdminApi.Media.MediaIngestService>();
        builder.Services.AddDcmsSearchData(builder.Configuration);
        builder.Services.AddDcmsAnalyticsData(builder.Configuration);
        builder.Services.AddDcmsVisitorsData(builder.Configuration);
        builder.Services.AddDcmsChatData(builder.Configuration);
        builder.Services.AddDcmsFormsData(builder.Configuration);
        builder.Services.AddDcmsAppsData(builder.Configuration);
        builder.Services.AddDcmsFunctionsData(builder.Configuration);
        builder.Services.AddDcmsUserAuthData(builder.Configuration);
        builder.Services.AddDcmsNotificationsData(builder.Configuration);
        // The job ledger (ADR 0024): admin-api owns the schema and enqueues most jobs; jobs-worker runs
        // them, folds in reports from the workers that cannot write the ledger, and sweeps it.
        builder.Services.AddDcmsJobs(builder.Configuration, serviceName);

        builder.Services.AddDcmsAuditData(builder.Configuration);
        // ADR 0015: sets app.tenant_id / app.scope per unit of work when Rls:Enforce is on; absent otherwise.
        builder.Services.AddDcmsRlsEnforcement(builder.Configuration);

        // Registered so the migration job can create the shared Data Protection key ring.
        // admin-api does not consume it -- it authenticates with bearer tokens and sets no
        // cookies -- but it owns the DDL for every schema in this database.
        builder.Services.AddDcmsDataProtection(builder.Configuration);
        // Same reason again: the edge's certificate, ACME-account and route tables are created by this
        // image running --migrate-only. admin-api reads none of them today; Phase 5 gives it the
        // certificate-status and custom-upload endpoints, which is when it starts to.
        builder.Services.AddDcmsEdgeData(builder.Configuration);
        builder.Services.AddDcmsVaultTransit();

        builder.Services.AddDcmsTenantResolutionByHeader();

        builder.Services.AddScoped<TenantProvisioning>();
        builder.Services.AddScoped<WorkspaceMembers>();

        // Plugins, hosted in full (docs/adr/0016): their admin-plane routes in admin-api, their event
        // handlers, jobs and hosted services in jobs-worker (runsBackgroundWork) -- the only two places
        // dcms.secrets@1 can decrypt. Also the catalog and config validation.
        builder.AddDcmsPlugins(Dcms.PluginSdk.Abstractions.PluginPlane.Admin, plugins => plugins
            .AddAll()
            // The admin plane's media contract can import files; the site plane's cannot.
            .AddPlatformContract<Dcms.PluginSdk.Abstractions.Platform.IPluginMedia, Dcms.AdminApi.Media.AdminPluginMedia>()
            // Git is here: a plugin's own repository, edited in the IDE (ADR 0029).
            .AddPlatformContract<Dcms.PluginSdk.Abstractions.Platform.IPluginRepos, Dcms.AdminApi.Sites.Git.AdminPluginRepos>()
            // The console's plugin instances as a contract, for the assistant (install, settings, enable).
            .AddPlatformContract<Dcms.PluginSdk.Abstractions.Platform.IPluginInstances, Dcms.AdminApi.Plugins.AdminPluginInstances>(),
            runsBackgroundWork);
        // The plugins that record what visitors do, for the consent flag every build carries
        // (SiteBuilds.AnalyticsEnabledAsync). The registry was built just above and is fixed for
        // the life of the process, so it is read once here, for both services.
        Dcms.AdminApi.Sites.SiteBuilds.VisitorTrackingPlugins = builder.Services
            .Select(d => d.ImplementationInstance).OfType<Dcms.PluginSdk.Abstractions.IPluginCatalog>().First()
            .Manifests.Where(m => m.TracksVisitors).Select(m => m.Id).ToList();
        // Plugin imports fetch URLs that came from outside: public addresses only (PublicEgress).
        builder.Services.AddHttpClient(Dcms.AdminApi.Media.AdminPluginMedia.HttpClientName, client =>
                client.Timeout = TimeSpan.FromMinutes(2))
            .ConfigurePrimaryHttpMessageHandler(sp => Dcms.Shared.Hosting.PublicEgress.Handler(
                allowPrivate: sp.GetRequiredService<IConfiguration>().GetValue("Media:ImportAllowLocal", false)));
        // Google Drive import (Media/GoogleDriveImport.cs). Optional: GoogleDrive__* in
        // secret/dcms/admin-api; unset, the console does not offer Drive.
        // PageSpeed Insights audits of sites (#649): Google's API, with a key from Vault (PageSpeed__ApiKey).
        builder.Services.Configure<Dcms.AdminApi.Sites.Audits.PageSpeedOptions>(
            builder.Configuration.GetSection(Dcms.AdminApi.Sites.Audits.PageSpeedOptions.Section));
        builder.Services.AddHttpClient<Dcms.AdminApi.Sites.Audits.PageSpeedClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Dcms.AdminApi.Sites.Audits.PageSpeedOptions>>().Value;
            client.BaseAddress = new Uri(options.ApiBase.EndsWith('/') ? options.ApiBase : options.ApiBase + "/");
            // A Lighthouse run on Google's side takes 10-60 s; a slow page more.
            client.Timeout = TimeSpan.FromSeconds(150);
        });
        builder.Services.Configure<Dcms.AdminApi.Media.GoogleDriveOptions>(
            builder.Configuration.GetSection(Dcms.AdminApi.Media.GoogleDriveOptions.SectionName));

        // Environments (ADR 0026): the configuration kinds a workspace's environments move between them
        // as migrations, in the order a migration applies them; plugins add theirs on top.
        builder.Services.AddScoped<Dcms.AdminApi.Environments.EnvironmentMigrator>();
        builder.Services.AddScoped<Dcms.AdminApi.Plugins.PluginInstanceService>();
        builder.Services.AddScoped<Dcms.PluginSdk.Abstractions.IEnvironmentConfigKind, Dcms.AdminApi.Environments.Kinds.RolesConfigKind>();
        builder.Services.AddScoped<Dcms.PluginSdk.Abstractions.IEnvironmentConfigKind, Dcms.AdminApi.Environments.Kinds.PluginsConfigKind>();
        builder.Services.AddScoped<Dcms.PluginSdk.Abstractions.IEnvironmentConfigKind, Dcms.AdminApi.Environments.Kinds.ApiConnectionsConfigKind>();
        builder.Services.AddScoped<Dcms.PluginSdk.Abstractions.IEnvironmentConfigKind, Dcms.AdminApi.Environments.Kinds.SitesConfigKind>();
        builder.Services.AddScoped<Dcms.PluginSdk.Abstractions.IEnvironmentConfigKind, Dcms.AdminApi.Environments.Kinds.ContentConfigKind>();
        builder.Services.AddScoped<Dcms.PluginSdk.Abstractions.IEnvironmentConfigKind, Dcms.AdminApi.Environments.Kinds.LinksConfigKind>();
        builder.Services.AddScoped<Dcms.AdminApi.Environments.EnvironmentDuplicator>();
        builder.Services.AddScoped<Dcms.PluginSdk.Abstractions.IEnvironmentDataCopier, Dcms.AdminApi.Environments.Copiers.ContentDraftsCopier>();
        builder.Services.AddScoped<Dcms.PluginSdk.Abstractions.IEnvironmentDataCopier, Dcms.AdminApi.Environments.Copiers.PluginDataCopier>();
        builder.Services.AddScoped<Dcms.PluginSdk.Abstractions.IEnvironmentDataCopier, Dcms.AdminApi.Environments.Copiers.VisitorsCopier>();
        builder.Services.AddScoped<Dcms.PluginSdk.Abstractions.IEnvironmentDataCopier, Dcms.AdminApi.Environments.Copiers.FormSubmissionsCopier>();
        builder.Services.AddScoped<Dcms.PluginSdk.Abstractions.IEnvironmentDataCopier, Dcms.AdminApi.Environments.Copiers.ChatCopier>();

        // Permission evaluation: dynamic policy + tenancy-backed, Redis-cached resolver.

        builder.Services.AddScoped<TenancyPermissionResolver>();
        builder.Services.AddScoped<IPermissionResolver>(sp => sp.GetRequiredService<TenancyPermissionResolver>());

        // In-app notifications. Each consumer turns one already-published subject into a
        // notification, so the producing services are untouched. All are shared durables: the work
        // is a database write that exactly one replica must do, and the SignalR Redis backplane is
        // what carries the push to browsers connected to the others.
        builder.Services.AddScoped<NotificationPublisher>();
        // Singleton: it holds nothing but an IHubContext, and endpoints, consumers and workers all
        // reach for it — a scoped registration would be a scope per push for no reason.
        builder.Services.AddSingleton<ResourceChangePublisher>();

        // External API connections (Mode D, backlog #124): fetched here, served as snapshots by
        // content-api. Public addresses only, and no redirects — a redirect would carry a header
        // credential to whatever host the provider names.
        builder.Services.AddHttpClient(Dcms.AdminApi.Connections.ApiConnectionFetcher.HttpClientName, client =>
                client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var handler = Dcms.Shared.Hosting.PublicEgress.Handler(
                    allowPrivate: sp.GetRequiredService<IConfiguration>().GetValue("Connections:AllowLocal", false));
                handler.AllowAutoRedirect = false;
                return handler;
            });
        builder.Services.AddScoped<Dcms.AdminApi.Connections.ApiConnectionFetcher>();
        // The platform console's own bell. Separate publisher and separate tables, because these
        // notifications have no tenant and their audience is a global role admin-api cannot enumerate
        // -- see PlatformNotification. The worker reads the edge's attempt ledger rather than
        // consuming an event: that ledger is the rate-limit guard, so it cannot be skipped.
        builder.Services.AddScoped<PlatformNotificationPublisher>();

        // Outbound client-credentials token provider for calling ai-gateway.
        builder.Services.Configure<ServiceClientOptions>(builder.Configuration.GetSection(ServiceClientOptions.SectionName));

        builder.Services.AddServiceTokenProvider();
        // identity's realm admin API (ADR 0022): the User Authentication plugin's directory, and the
        // realm a deleted tenant leaves behind.
        builder.Services.AddRealmAdminClient(builder.Configuration, builder.Environment.IsDevelopment());
        builder.Services.AddHttpClient("ai-gateway", (sp, client) =>
        {
            var baseUrl = sp.GetRequiredService<IConfiguration>()["Services:AiGateway"] ?? "http://localhost:5007";
            client.BaseAddress = new Uri(baseUrl);
        });

        // Reverse-proxy target for the site preview (delivery API). Same content-api the
        // published site talks to; the preview proxy injects the tenant + sandbox headers.
        builder.Services.AddHttpClient("content-api", (sp, client) =>
        {
            var baseUrl = sp.GetRequiredService<IConfiguration>()["Services:ContentApi"] ?? "http://content-api:8080";
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        });


        // Forgejo git server: source of truth for Mode B site source. The machine token
        // arrives via Vault (secret/dcms/admin-api, Forgejo__Token); git operations are
        // no-ops until it's set (ForgejoOptions.Enabled).
        builder.Services.Configure<Dcms.AdminApi.Sites.Git.ForgejoOptions>(
            builder.Configuration.GetSection(Dcms.AdminApi.Sites.Git.ForgejoOptions.SectionName));


        builder.Services.AddHttpClient<Dcms.AdminApi.Sites.Git.ForgejoClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Dcms.AdminApi.Sites.Git.ForgejoOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
            if (!string.IsNullOrWhiteSpace(opts.Token))
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", opts.Token);
        });
        builder.Services.AddScoped<Dcms.AdminApi.Sites.Git.SiteGitService>();
        builder.Services.AddScoped<Dcms.AdminApi.Sites.Git.RepoAccessReconciler>();
        builder.Services.AddScoped<Dcms.AdminApi.Sites.SiteDeleter>();
        // Live deployment/commit fan-out to everyone with a site open in the IDE. Singleton because
        // it holds nothing per-request -- IHubContext is itself a singleton -- and because the site
        // event consumers reach it from background scopes.
        builder.Services.AddSingleton<Dcms.AdminApi.Sites.SiteLiveUpdates>();

        builder.Services.AddScoped<TenantDeleter>();
        builder.Services.AddScoped<TenantUsageMeasurer>();

        return builder;
    }
}
