using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Protos;

namespace WarpTalk.Shared.AdminAudit;

public static class AdminAuditServiceCollectionExtensions
{
    /// <summary>
    /// Wires <see cref="AdminAuditedAttribute"/> for this service: the per-request scope, the global
    /// MVC filter, the save interceptor and a gRPC sink stamped with <paramref name="sourceService"/>.
    /// The caller registers the <see cref="AdminAuditService.AdminAuditServiceClient"/> itself — it
    /// knows its own configuration key for the workspace service — and adds the interceptor to its
    /// DbContext with <see cref="AddAdminAuditInterceptor"/>.
    /// </summary>
    public static IServiceCollection AddWarpTalkAdminAuditing(this IServiceCollection services, string sourceService)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<AdminAuditScope>();
        services.AddScoped<IAdminAuditSink>(sp => new GrpcAdminAuditSink(
            sp.GetRequiredService<AdminAuditService.AdminAuditServiceClient>(),
            sourceService,
            sp.GetRequiredService<ILogger<GrpcAdminAuditSink>>()));
        services.AddSingleton<AdminAuditSaveChangesInterceptor>();
        services.AddScoped<AdminAuditActionFilter>();
        services.Configure<MvcOptions>(options => options.Filters.AddService<AdminAuditActionFilter>());
        return services;
    }

    /// <summary>
    /// Adds the interceptor when <see cref="AddWarpTalkAdminAuditing"/> registered it. A host that
    /// did not (a test, a worker-only process) builds the same context without it.
    /// </summary>
    public static DbContextOptionsBuilder AddAdminAuditInterceptor(
        this DbContextOptionsBuilder options, IServiceProvider services)
    {
        var interceptor = services.GetService<AdminAuditSaveChangesInterceptor>();
        return interceptor is null ? options : options.AddInterceptors(interceptor);
    }
}
