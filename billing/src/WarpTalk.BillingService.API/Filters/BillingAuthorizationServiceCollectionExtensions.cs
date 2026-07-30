using Microsoft.Extensions.DependencyInjection;

namespace WarpTalk.BillingService.API.Filters;

public static class BillingAuthorizationServiceCollectionExtensions
{
    public static IServiceCollection AddBillingAuthorizationDependencies(
        this IServiceCollection services)
    {
        services.AddMemoryCache();
        return services;
    }
}
