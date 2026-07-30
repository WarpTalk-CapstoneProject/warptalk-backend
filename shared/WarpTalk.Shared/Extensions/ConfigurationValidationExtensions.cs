using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace WarpTalk.Shared.Extensions;

public static class ConfigurationValidationExtensions
{
    public static Uri GetRequiredServiceUri(
        this IConfiguration configuration,
        IHostEnvironment environment,
        string key,
        string developmentFallback)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"{key} is required outside Development.");
            }

            raw = developmentFallback;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                $"{key} must be an absolute HTTP or HTTPS URL.");
        }

        return uri;
    }

    public static void RequirePublicBaseUrl(
        this IConfiguration configuration,
        IHostEnvironment environment,
        string key)
    {
        var raw = configuration[key];
        if (environment.IsDevelopment())
        {
            return;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{key} must be a public HTTP or HTTPS URL outside Development.");
        }
    }
}
