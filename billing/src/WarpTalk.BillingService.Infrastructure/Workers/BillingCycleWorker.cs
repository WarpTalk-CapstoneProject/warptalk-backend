using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Infrastructure.Options;

namespace WarpTalk.BillingService.Infrastructure.Workers;

public sealed class BillingCycleWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BillingCycleWorker> _logger;
    private readonly BillingWorkerOptions _options;

    public BillingCycleWorker(
        IServiceProvider serviceProvider,
        ILogger<BillingCycleWorker> logger,
        IOptions<BillingWorkerOptions> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BillingCycleWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CloseDueCyclesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BillingCycleWorker failed to close due billing cycles.");
            }

            await Task.Delay(_options.BillingCycleInterval, stoppingToken);
        }
    }

    public async Task CloseDueCyclesAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var cycleClosingService = scope.ServiceProvider.GetRequiredService<IBillingCycleClosingService>();

        var now = DateTime.UtcNow;
        var result = await cycleClosingService.CloseDueCyclesAsync(
            now,
            _options.SubscriptionRenewalLookback,
            cancellationToken);

        if (!result.IsSuccess)
        {
            _logger.LogError("BillingCycleWorker failed to close due cycles: {Error}", result.Error);
            return;
        }

        if (result.Value > 0)
            _logger.LogInformation("BillingCycleWorker closed {Count} billing cycle(s).", result.Value);
    }
}
