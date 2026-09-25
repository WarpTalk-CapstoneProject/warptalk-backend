using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.Shared.Coordination;

namespace WarpTalk.BillingService.Infrastructure.Workers;

/// <summary>
/// G11 — every <c>Billing:CreditPacks:ExpiryIntervalMinutes</c> (default 60; 0 disables) one replica,
/// under a Redis lease, expires the credit-pack purchases whose validity has ended. See
/// <see cref="CreditPackExpiryService"/> for which credits expire. Every exception is caught: one
/// escaping ExecuteAsync would stop the billing host.
/// </summary>
public sealed class CreditPackExpiryWorker : BackgroundService
{
    public const string LockResource = "billing:credit-pack-expiry";

    private readonly IServiceProvider _serviceProvider;
    private readonly IDistributedLockProvider _locks;
    private readonly ILogger<CreditPackExpiryWorker> _logger;
    private readonly TimeSpan _interval;

    public CreditPackExpiryWorker(
        IServiceProvider serviceProvider,
        IDistributedLockProvider locks,
        IConfiguration configuration,
        ILogger<CreditPackExpiryWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _locks = locks;
        _logger = logger;
        var minutes = configuration.GetValue<int?>("Billing:CreditPacks:ExpiryIntervalMinutes") ?? 60;
        _interval = minutes <= 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_interval == TimeSpan.Zero)
        {
            _logger.LogInformation("CreditPackExpiryWorker is disabled (Billing:CreditPacks:ExpiryIntervalMinutes = 0).");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _locks.TryRunExclusiveAsync(LockResource, TimeSpan.FromMinutes(5), SweepAsync, _logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CreditPackExpiryWorker: sweep failed; the next run retries.");
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

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var swept = await scope.ServiceProvider.GetRequiredService<ICreditPackExpiryService>().SweepAsync(ct);
        if (swept > 0)
        {
            _logger.LogInformation("CreditPackExpiryWorker: {Count} credit pack purchase(s) expired.", swept);
        }
    }
}
