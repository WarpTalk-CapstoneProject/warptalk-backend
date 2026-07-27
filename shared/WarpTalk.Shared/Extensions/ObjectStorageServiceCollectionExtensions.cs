using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.Shared.Configuration;

namespace WarpTalk.Shared.Extensions;

public static class ObjectStorageServiceCollectionExtensions
{
    public static IServiceCollection AddWarpTalkObjectStorageOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ObjectStorageOptions>(configuration.GetSection(ObjectStorageOptions.SectionName));
        return services;
    }
}
