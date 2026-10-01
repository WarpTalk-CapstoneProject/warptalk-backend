using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace WarpTalk.Shared.PlatformSettings;

public static class PlatformSettingsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IPlatformSettings"/> as a singleton reader over the published Redis
    /// hashes. Uses the host's <see cref="IConnectionMultiplexer"/> when it has one; a host without
    /// Redis gets a reader where nothing is ever set, so every caller keeps its own configuration.
    /// </summary>
    public static IServiceCollection AddWarpTalkPlatformSettings(this IServiceCollection services)
    {
        services.TryAddSingleton<IPlatformSettingsSource>(sp =>
            sp.GetService<IConnectionMultiplexer>() is { } redis
                ? new RedisPlatformSettingsSource(redis)
                : new EmptyPlatformSettingsSource());
        services.TryAddSingleton<PlatformSettingsReader>(sp => new PlatformSettingsReader(
            sp.GetRequiredService<IPlatformSettingsSource>(),
            sp.GetRequiredService<ILogger<PlatformSettingsReader>>()));
        services.TryAddSingleton<IPlatformSettings>(sp => sp.GetRequiredService<PlatformSettingsReader>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PlatformSettingsWarmupService>());
        return services;
    }
}

/// <summary>
/// Reads the published snapshot as the host starts, so the synchronous getters (rate limits,
/// maintenance mode, validators) have the operator's values within moments of boot instead of
/// waiting for the first request to trigger a background refresh. It never delays start-up and
/// never fails it: a Redis outage is logged by the reader and the fallbacks apply meanwhile.
/// </summary>
public sealed class PlatformSettingsWarmupService : BackgroundService
{
    private readonly PlatformSettingsReader _reader;
    private readonly ILogger<PlatformSettingsWarmupService> _logger;

    public PlatformSettingsWarmupService(PlatformSettingsReader reader, ILogger<PlatformSettingsWarmupService> logger)
    {
        _reader = reader;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Yield();
            await _reader.RefreshAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Platform settings could not be read at start-up; fallbacks apply until the next refresh.");
        }
    }
}
