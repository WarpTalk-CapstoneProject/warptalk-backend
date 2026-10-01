using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Coordination;
using WarpTalk.TranscriptService.Application.FarSpeakers;

namespace WarpTalk.TranscriptService.Infrastructure.Workers;

/// <summary>
/// Drives <see cref="IFarSpeakerRelabelService"/>: every tick, queue newly seen bridge rooms and run
/// the jobs that are due. One replica at a time — the tick holds a Redis lease, and the jobs it
/// writes are idempotent anyway (a relabel re-run writes the same names).
/// </summary>
public sealed class FarSpeakerRelabelWorker : BackgroundService
{
    internal const string LockResource = "transcript:far-speaker-relabel";
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly IDistributedLockProvider _locks;
    private readonly ILogger<FarSpeakerRelabelWorker> _logger;

    public FarSpeakerRelabelWorker(
        IServiceScopeFactory scopes,
        IDistributedLockProvider locks,
        ILogger<FarSpeakerRelabelWorker> logger)
    {
        _scopes = scopes;
        _locks = locks;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _locks.TryRunExclusiveAsync(LockResource, Lease, TickAsync, _logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Far-speaker relabel tick failed");
            }

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

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFarSpeakerRelabelService>();
        await service.DiscoverAsync(ct);
        await service.ProcessDueAsync(ct);
    }
}
