using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace WarpTalk.Shared.Extensions;

public static class ObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddWarpTalkObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        string serviceName)
    {
        var enabled = configuration.GetValue<bool?>("Observability:Enabled")
                      ?? !environment.IsDevelopment();
        if (!enabled)
        {
            return services;
        }

        var serviceVersion =
            typeof(ObservabilityServiceCollectionExtensions).Assembly
                .GetName()
                .Version?
                .ToString()
            ?? "unknown";
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: serviceName,
                serviceVersion: serviceVersion,
                serviceInstanceId: Environment.MachineName)
            .AddAttributes(
            [
                new KeyValuePair<string, object>(
                    "deployment.environment.name",
                    environment.EnvironmentName)
            ]);

        services.AddOpenTelemetry()
            .ConfigureResource(builder => builder
                .AddService(
                    serviceName,
                    serviceVersion: serviceVersion,
                    serviceInstanceId: Environment.MachineName)
                .AddAttributes(
                [
                    new KeyValuePair<string, object>(
                        "deployment.environment.name",
                        environment.EnvironmentName)
                ]))
            .WithTracing(tracing => tracing
                .AddSource(serviceName)
                .AddSource("MassTransit")
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.RecordException = true;
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments("/health/live");
                })
                .AddHttpClientInstrumentation(options =>
                {
                    options.RecordException = true;
                })
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddMeter(serviceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter());

        services.AddLogging(logging => logging.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(resourceBuilder);
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.ParseStateValues = true;
            options.AddOtlpExporter();
        }));

        return services;
    }
}
