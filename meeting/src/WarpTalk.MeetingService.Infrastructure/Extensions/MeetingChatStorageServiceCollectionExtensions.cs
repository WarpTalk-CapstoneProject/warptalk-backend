using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Storage;
using WarpTalk.Shared.Configuration;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.MeetingService.Infrastructure.Extensions;

public static class MeetingChatStorageServiceCollectionExtensions
{
    public static IServiceCollection AddMeetingChatFileStorage(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddWarpTalkObjectStorageOptions(configuration);

        var options = configuration
            .GetSection(ObjectStorageOptions.SectionName)
            .Get<ObjectStorageOptions>() ?? new ObjectStorageOptions();

        if (!options.UsesS3CompatibleProvider)
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "Meeting chat file storage must use an S3-compatible provider outside Development.");
            }

            services.AddSingleton<IMeetingChatFileStorage, LocalMeetingChatFileStorage>();
            return services;
        }

        ValidateS3Options(options, environment);
        services.AddSingleton<IAmazonS3>(_ =>
        {
            var serviceUrl = options.S3.ServiceUrl!;
            var config = new AmazonS3Config
            {
                ServiceURL = serviceUrl,
                ForcePathStyle = true,
                UseHttp = serviceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            };
            return new AmazonS3Client(
                options.S3.AccessKey,
                options.S3.SecretKey,
                config);
        });
        services.AddSingleton<IMeetingChatFileStorage, S3MeetingChatFileStorage>();
        return services;
    }

    private static void ValidateS3Options(
        ObjectStorageOptions options,
        IHostEnvironment environment)
    {
        var serviceUrlValid =
            Uri.TryCreate(options.S3.ServiceUrl, UriKind.Absolute, out var serviceUri)
            && (serviceUri.Scheme == Uri.UriSchemeHttp || serviceUri.Scheme == Uri.UriSchemeHttps);
        if (!serviceUrlValid)
        {
            throw new InvalidOperationException(
                "Storage:S3:ServiceUrl must be an absolute HTTP or HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(options.S3.BucketName))
        {
            throw new InvalidOperationException(
                "Storage:S3:BucketName is required for meeting chat file storage.");
        }

        var credentialsInvalid =
            IsMissingOrPlaceholder(options.S3.AccessKey)
            || IsMissingOrPlaceholder(options.S3.SecretKey);
        if (credentialsInvalid)
        {
            var environmentHint = environment.IsDevelopment()
                ? "Development still requires explicit S3-compatible credentials."
                : "Non-placeholder production credentials are required.";
            throw new InvalidOperationException(
                $"Storage:S3 credentials are invalid. {environmentHint}");
        }
    }

    private static bool IsMissingOrPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
        || value.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith('<') && value.EndsWith('>');
}
