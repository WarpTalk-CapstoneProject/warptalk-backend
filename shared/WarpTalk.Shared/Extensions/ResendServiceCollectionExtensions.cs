using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WarpTalk.Shared.Configuration;
using WarpTalk.Shared.Interfaces;
using WarpTalk.Shared.Services;

namespace WarpTalk.Shared.Extensions;

public static class ResendServiceCollectionExtensions
{
    public static IServiceCollection AddResendClient(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        var apiKey = configuration[$"{ResendSettings.SectionName}:ApiKey"]
                     ?? Environment.GetEnvironmentVariable("RESEND_API_KEY");
        if (environment?.IsProduction() == true &&
            (string.IsNullOrWhiteSpace(apiKey)
             || apiKey.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
             || apiKey.StartsWith("CHANGE_ME", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "CRITICAL SECURITY ERROR: a non-placeholder Resend API key is required in Production.");
        }

        services.Configure<ResendSettings>(options =>
        {
            var section = configuration.GetSection(ResendSettings.SectionName);
            options.ApiKey = apiKey ?? string.Empty;
            options.FromEmail = section["FromEmail"] ?? Environment.GetEnvironmentVariable("RESEND_FROM_EMAIL") ?? "no-reply@warptalk.vn";
            options.FromName = section["FromName"] ?? Environment.GetEnvironmentVariable("RESEND_FROM_NAME") ?? "WarpTalk";
        });

        services.AddHttpClient<IResendEmailClient, ResendEmailClient>();

        return services;
    }
}
