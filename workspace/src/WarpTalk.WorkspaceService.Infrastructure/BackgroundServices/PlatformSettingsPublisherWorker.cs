using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.WorkspaceService.Application.Services;

namespace WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;

/// <summary>
/// Re-publishes the stored platform settings to Redis at start-up and then every
/// <see cref="Interval"/>.
///
/// Every write already publishes; this is what heals everything else. Redis runs allkeys-lru and
/// can evict the settings hashes, or restart empty, and a reader then keeps its last snapshot only
/// for as long as its own process lives. Within one interval of either, the snapshot is back.
///
/// Every tick is guarded: a failed publish is logged and retried next tick. It must never stop the
/// host — an unguarded background loop here would take the workspace service down with it.
/// </summary>
public sealed class PlatformSettingsPublisherWorker : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PlatformSettingsPublisherWorker> _logger;

    public PlatformSettingsPublisherWorker(IServiceScopeFactory scopes, ILogger<PlatformSettingsPublisherWorker> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await TickAsync(stoppingToken);
            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IPlatformSettingsAdminService>().PublishAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Platform settings could not be re-published; retrying in {Interval}.", Interval);
        }
    }
}
