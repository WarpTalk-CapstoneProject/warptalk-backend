using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Infrastructure.Storage;
using WarpTalk.Shared.Configuration;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AuthService.Infrastructure.Extensions;

public static class VoiceSampleStorageServiceCollectionExtensions
{
    public static IServiceCollection AddVoiceSampleStorage(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddWarpTalkObjectStorageOptions(configuration);
        var options = configuration.GetSection(ObjectStorageOptions.SectionName)
            .Get<ObjectStorageOptions>() ?? new ObjectStorageOptions();
        if (!options.UsesS3CompatibleProvider)
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "Voice sample storage must use an S3-compatible provider outside Development.");
            }
            services.AddSingleton<IVoiceSampleStorage, LocalVoiceSampleStorage>();
            return services;
        }

        if (!Uri.TryCreate(options.S3.ServiceUrl, UriKind.Absolute, out var serviceUri)
            || (serviceUri.Scheme != Uri.UriSchemeHttp && serviceUri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(options.S3.BucketName)
            || IsPlaceholder(options.S3.AccessKey)
            || IsPlaceholder(options.S3.SecretKey))
        {
            throw new InvalidOperationException(
                "Storage:S3 service URL, bucket and non-placeholder credentials are required for voice samples.");
        }

        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
            options.S3.AccessKey,
            options.S3.SecretKey,
            new AmazonS3Config
            {
                ServiceURL = serviceUri.ToString(),
                ForcePathStyle = true,
                UseHttp = serviceUri.Scheme == Uri.UriSchemeHttp
            }));
        services.AddSingleton<IVoiceSampleStorage, S3VoiceSampleStorage>();
        return services;
    }

    private static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
        || value.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith('<') && value.EndsWith('>');
}
