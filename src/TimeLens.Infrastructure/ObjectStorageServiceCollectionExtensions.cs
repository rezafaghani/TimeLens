using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TimeLens.Domain.Interfaces;
using TimeLens.Infrastructure.Services;

namespace TimeLens.Infrastructure;

public static class ObjectStorageServiceCollectionExtensions
{
    public static IServiceCollection AddTimeLensObjectStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ObjectStorageOptions>(configuration.GetSection("ObjectStorage"));
        services.AddSingleton<IAmazonS3>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<ObjectStorageOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.AccessKey) || string.IsNullOrWhiteSpace(options.SecretKey))
            {
                throw new InvalidOperationException("Object storage credentials are not configured.");
            }

            var clientOptions = new AmazonS3Config
            {
                ServiceURL = options.ServiceUrl,
                ForcePathStyle = true,
                AuthenticationRegion = options.Region
            };
            return new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), clientOptions);
        });
        services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        return services;
    }
}
