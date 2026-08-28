using Dcms.Shared.Vault;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.DataProtection;

public static class DataProtectionServiceCollectionExtensions
{
    /// <summary>
    /// The application name every DCMS service shares.
    ///
    /// <para>This is not cosmetic. The application name is mixed into the purpose chain of
    /// every protector, so two processes with different names cannot read each other's
    /// payloads even when they share a key ring. The default is derived from the content
    /// root path, which differs between a container, a locally-run service and a future
    /// Kubernetes pod — meaning the default silently reintroduces the exact isolation this
    /// method exists to remove.</para>
    /// </summary>
    private const string ApplicationName = "dcms";

    /// <summary>
    /// Persists the Data Protection key ring to Postgres so every replica of every service
    /// shares one key ring. See <see cref="DataProtectionDbContext"/> for what breaks without it.
    ///
    /// <para>Postgres rather than Redis, deliberately. Redis here is a cache: it is sized for
    /// eviction, it is flushed during incidents, and losing it is supposed to be survivable.
    /// A flushed key ring would log every user out — recoverable — and simultaneously strand
    /// every <c>ForgejoSyncOutbox</c> row as permanently undecryptable ciphertext, which is
    /// not.</para>
    /// </summary>
    public static IServiceCollection AddDcmsDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<DataProtectionDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", DataProtectionDbContext.Schema)));

        services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToDbContext<DataProtectionDbContext>();

        // Encryption of the key ring at rest, off by default.
        //
        // It makes minting or reading a key depend on Vault being reachable and unsealed. On a
        // Shamir-sealed Vault that turns a reboot into "nobody can log in" — the same outage
        // auto-unseal exists to prevent, arriving through a different door. So enable this and
        // Transit auto-unseal together; see infra/vault/server/seal-transit.hcl.example.
        if (configuration.GetValue("DataProtection:ProtectWithTransit", false))
        {
            // Configured through the options pipeline rather than passed an instance, because
            // the encryptor needs ITransitEncryptor out of the container and this runs during
            // registration, before there is a provider to resolve it from.
            services.AddOptions<KeyManagementOptions>()
                .Configure<IServiceProvider>((options, sp) =>
                    options.XmlEncryptor = new TransitXmlEncryptor(sp.GetRequiredService<ITransitEncryptor>()));
        }

        // The decryptor is registered unconditionally, and deliberately: a key ring can hold
        // keys written while the flag was on alongside keys written while it was off. Tying the
        // decryptor's registration to the flag would make previously wrapped keys unreadable
        // the moment it was turned off — a config change that silently becomes a platform-wide
        // forced logout, and an unreadable ForgejoSyncOutbox.
        services.AddSingleton<TransitXmlDecryptor>();

        return services;
    }
}
