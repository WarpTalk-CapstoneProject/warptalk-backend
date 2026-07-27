using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.Shared.Configuration;
using WarpTalk.Shared.Interfaces;
using WarpTalk.Shared.Services;

namespace WarpTalk.Shared.Extensions;

public static class ResendServiceCollectionExtensions
{
    public static IServiceCollection AddResendClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ResendSettings>(options =>
        {
            var section = configuration.GetSection(ResendSettings.SectionName);
            options.ApiKey = section["ApiKey"] ?? Environment.GetEnvironmentVariable("RESEND_API_KEY") ?? string.Empty;
            options.FromEmail = section["FromEmail"] ?? Environment.GetEnvironmentVariable("RESEND_FROM_EMAIL") ?? "no-reply@warptalk.vn";
            options.FromName = section["FromName"] ?? Environment.GetEnvironmentVariable("RESEND_FROM_NAME") ?? "WarpTalk";
        });

        services.AddHttpClient<IResendEmailClient, ResendEmailClient>();

        return services;
    }
}
