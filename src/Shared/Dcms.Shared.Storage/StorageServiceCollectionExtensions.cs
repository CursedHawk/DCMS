using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Minio;

namespace Dcms.Shared.Storage;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsObjectStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));

        services.AddSingleton<IMinioClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            return new MinioClient()
                .WithEndpoint(opts.Endpoint)
                .WithCredentials(opts.AccessKey, opts.SecretKey)
                .WithSSL(opts.UseSsl)
                .Build();
        });
        services.AddSingleton<IObjectStorage, MinioObjectStorage>();

        services.AddHealthChecks().AddCheck<MinioHealthCheck>("minio", tags: ["ready"]);

        return services;
    }
}
