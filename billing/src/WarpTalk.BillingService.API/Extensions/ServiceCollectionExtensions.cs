using Microsoft.Extensions.DependencyInjection;


namespace WarpTalk.BillingService.API.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCustomApiBehavior(this IServiceCollection services)
    {
        services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            });

        return services;
    }
}
