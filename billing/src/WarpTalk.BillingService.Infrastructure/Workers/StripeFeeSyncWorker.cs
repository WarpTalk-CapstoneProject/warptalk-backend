using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Options;
using WarpTalk.BillingService.Infrastructure.Services;
using WarpTalk.Shared.Coordination;

namespace WarpTalk.BillingService.Infrastructure.Workers;

/// <summary>
/// Reads the fee Stripe kept on every paid Stripe payment into subscription.payment_provider_fees —
/// the Stripe cost line of the admin Providers page.
///
/// BOUNDED AND IDEMPOTENT
///   Each tick (every <see cref="ProviderStatusOptions.StripeFeeSyncIntervalMinutes"/>, one replica)
///   takes at most <see cref="ProviderStatusOptions.StripeFeeBatchSize"/> payments, oldest first, that
///   were paid in the last <see cref="BackfillDays"/> days and have no fee row — so the 90-day backfill
///   drains a batch at a time and then only new payments are read. A payment is one row (unique
///   payment_id): a re-read replaces it. Not found (no charge behind the id) is final; a Stripe error
///   is retried after <see cref="RetryErrorsAfter"/>. No key = disabled, logged once. Nothing escapes
///   the loop (an exception out of ExecuteAsync stops the billing host).
/// </summary>
public sealed class StripeFeeSyncWorker : BackgroundService
{
    public const string LockResource = "billing:stripe-fee-sync";
    public const int BackfillDays = 90;
    public static readonly TimeSpan RetryErrorsAfter = TimeSpan.FromHours(6);

    private readonly IServiceProvider _serviceProvider;
    private readonly IDistributedLockProvider _locks;
    private readonly ProviderStatusOptions _options;
    private readonly bool _configured;
    private readonly ILogger<StripeFeeSyncWorker> _logger;
    private readonly TimeProvider _time;

    public StripeFeeSyncWorker(
        IServiceProvider serviceProvider,
        IDistributedLockProvider locks,
        IOptions<ProviderStatusOptions> options,
        IConfiguration configuration,
        ILogger<StripeFeeSyncWorker> logger,
        TimeProvider? time = null)
    {
        _serviceProvider = serviceProvider;
        _locks = locks;
        _options = options.Value;
        _configured = !string.IsNullOrWhiteSpace(configuration[PaymentConstants.StripeConfigKeys.SecretKey]);
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configured || _options.StripeFeeSyncIntervalMinutes <= 0)
        {
            _logger.LogInformation("StripeFeeSyncWorker is disabled (no Stripe:SecretKey, or ProviderStatus:StripeFeeSyncIntervalMinutes = 0).");
            return;
        }

        var interval = TimeSpan.FromMinutes(_options.StripeFeeSyncIntervalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _locks.TryRunExclusiveAsync(LockResource, TimeSpan.FromMinutes(5), SyncBatchAsync, _logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StripeFeeSyncWorker: batch failed; retrying next tick.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One bounded batch. Returns how many payments were read.</summary>
    public async Task<int> SyncBatchAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var reader = new StripeFeeReader(scope.ServiceProvider.GetRequiredService<IStripeSdkClient>());

        var awaiting = await unitOfWork.PaymentProviderFees.GetAwaitingAsync(
            ProviderCatalog.Stripe, now.AddDays(-BackfillDays), now - RetryErrorsAfter, Math.Clamp(_options.StripeFeeBatchSize, 1, 100), ct);
        var failed = 0;
        foreach (var payment in awaiting)
        {
            var fee = await reader.ReadAsync(payment.PaymentId, payment.ProviderTransactionId, now, ct);
            if (fee.Status == Domain.Entities.PaymentProviderFee.StatusError) failed++;
            await unitOfWork.PaymentProviderFees.UpsertAsync(fee, ct);
        }

        if (awaiting.Count > 0)
        {
            _logger.LogInformation("StripeFeeSyncWorker read {Count} payment fee(s); {Failed} will be retried.", awaiting.Count, failed);
        }

        return awaiting.Count;
    }
}
