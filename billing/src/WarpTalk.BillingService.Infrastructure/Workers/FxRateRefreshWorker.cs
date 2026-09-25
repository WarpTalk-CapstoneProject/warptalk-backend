using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared.Coordination;

namespace WarpTalk.BillingService.Infrastructure.Workers;

/// <summary>
/// Records Stripe's USD→VND rate once per UTC day (and the rates of recently converted VND charges),
/// so every VND report converts a day at that day's rate. See <see cref="IFxRateService.RefreshAsync"/>.
///
/// Every <c>Billing:Fx:CheckIntervalMinutes</c> (default 60) one replica — a Redis lease — asks whether
/// today already has a Stripe quote; only when it does not does it call Stripe. So a failed day is
/// retried hourly, and a healthy day costs one quote. Failures are logged by the service and surface
/// on /admin/settings as a stale-rate warning; reports keep the last known rate. Every exception is
/// caught: one escaping ExecuteAsync would stop the whole billing host.
/// </summary>
public sealed class FxRateRefreshWorker : BackgroundService
{
    public const string LockResource = "billing:fx-refresh";

    private readonly IServiceProvider _serviceProvider;
    private readonly IDistributedLockProvider _locks;
    private readonly ILogger<FxRateRefreshWorker> _logger;
    private readonly TimeSpan _interval;

    public FxRateRefreshWorker(
        IServiceProvider serviceProvider,
        IDistributedLockProvider locks,
        IConfiguration configuration,
        ILogger<FxRateRefreshWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _locks = locks;
        _logger = logger;
        var minutes = configuration.GetValue<int?>("Billing:Fx:CheckIntervalMinutes") ?? 60;
        _interval = minutes <= 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_interval == TimeSpan.Zero)
        {
            _logger.LogInformation("FxRateRefreshWorker is disabled (Billing:Fx:CheckIntervalMinutes = 0); the USD→VND rate stays as last recorded.");
            return;
        }

        _logger.LogInformation("FxRateRefreshWorker started; checking for today's Stripe USD→VND rate every {Interval}.", _interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _locks.TryRunExclusiveAsync(LockResource, TimeSpan.FromMinutes(5), RefreshAsync, _logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FxRateRefreshWorker: refresh failed; reports keep the last known USD→VND rate.");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var fx = scope.ServiceProvider.GetRequiredService<IFxRateService>();
        var result = await fx.RefreshAsync(force: false, ct);
        if (result.Error is not null)
        {
            _logger.LogWarning("USD→VND refresh: {Error}", result.Error);
        }
        else if (result.QuoteRecorded)
        {
            _logger.LogInformation("USD→VND rate recorded from Stripe: {Rate} ({Source}); {ChargeDays} charge day(s) updated.",
                result.Status.Rate, result.Status.Source, result.ChargeDaysRecorded);
        }
    }
}
