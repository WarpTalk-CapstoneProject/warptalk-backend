using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Application.Mappers;

namespace WarpTalk.BillingService.Infrastructure.Workers;

/// <summary>
/// Chạy mỗi 1 giờ. Tìm các subscription có AutoRenew=true, sắp hết hạn
/// trong 24h tới và chưa được gia hạn → tự động cộng credits mới,
/// reset CreditsUsedThisCycle, và extend CurrentPeriodEnd sang chu kỳ tiếp theo.
///
/// Worker này là safety net — đảm bảo credits được cộng đúng thời điểm
/// ngay cả khi Stripe webhook bị delay. Không thay thế Stripe webhook.
/// (Plan Mục 3A: Subscription Renewal)
/// </summary>
public class SubscriptionRenewalWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubscriptionRenewalWorker> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(1);

    // Chỉ gia hạn các subscription sắp hết hạn trong vòng cửa sổ này
    private readonly TimeSpan _renewalWindow = TimeSpan.FromHours(24);

    public SubscriptionRenewalWorker(
        IServiceProvider serviceProvider,
        ILogger<SubscriptionRenewalWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SubscriptionRenewalWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RenewSubscriptionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SubscriptionRenewalWorker: error during renewal cycle.");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }

        _logger.LogInformation("SubscriptionRenewalWorker is stopping.");
    }

    private async Task RenewSubscriptionsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var now = DateTime.UtcNow;
        var renewalThreshold = now.Add(_renewalWindow);

        // Tìm subscriptions cần gia hạn:
        // - Đang active, chưa xóa mềm
        // - AutoRenew = true
        // - CurrentPeriodEnd sắp hết trong _renewalWindow tới
        //   hoặc đã hết hạn (missed) nhưng chưa bị expire worker xử lý
        var dueForRenewal = await unitOfWork.SubscriptionRepository
            .Query()
            .Include(s => s.Plan)
            .Where(s =>
                s.IsActive &&
                s.DeletedAt == null &&
                s.AutoRenew &&
                s.Status == SubscriptionConstants.SubscriptionStatuses.Active &&
                s.CurrentPeriodEnd <= renewalThreshold &&
                s.CurrentPeriodEnd > now.AddDays(-1)) // không gia hạn quá trễ (>1 ngày)
            .ToListAsync(cancellationToken);

        if (dueForRenewal.Count == 0)
            return;

        _logger.LogInformation(
            "SubscriptionRenewalWorker: found {Count} subscription(s) due for renewal.",
            dueForRenewal.Count);

        var renewed = 0;
        foreach (var sub in dueForRenewal)
        {
            try
            {
                await RenewOneAsync(unitOfWork, sub, cancellationToken);
                renewed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "SubscriptionRenewalWorker: failed to renew subscription {SubId}.", sub.Id);
            }
        }

        if (renewed > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "SubscriptionRenewalWorker: successfully renewed {Count} subscription(s).", renewed);
        }
    }

    private static async Task RenewOneAsync(
        IUnitOfWork unitOfWork,
        Subscription sub,
        CancellationToken cancellationToken)
    {
        var plan = sub.Plan;
        var creditsToAdd = plan.CreditsPerCycle;

        // Tính chu kỳ tiếp theo dựa theo BillingCycle của plan
        var (newStart, newEnd) = CalculateNextCycleDates(sub.CurrentPeriodEnd, plan.BillingCycle);

        // Cộng credits mới và reset cycle usage
        sub.CreditsRemaining += creditsToAdd;
        sub.CreditsUsedThisCycle = 0;
        sub.CurrentPeriodStart = newStart;
        sub.CurrentPeriodEnd = newEnd;
        sub.UpdatedAt = DateTime.UtcNow;

        unitOfWork.SubscriptionRepository.Update(sub);

        // Ghi CreditTransaction loại top_up để có audit trail
        var renewalTx = sub.CreateRenewalTransaction(plan, newStart);

        await unitOfWork.CreditTransactionRepository.AddAsync(renewalTx, cancellationToken);
    }

    /// <summary>
    /// Tính ngày bắt đầu/kết thúc chu kỳ tiếp theo dựa trên BillingCycle của plan.
    /// </summary>
    private static (DateTime newStart, DateTime newEnd) CalculateNextCycleDates(
        DateTime currentPeriodEnd, string billingCycle)
    {
        var newStart = currentPeriodEnd;
        var newEnd = billingCycle switch
        {
            SubscriptionConstants.BillingCycles.Monthly    => newStart.AddMonths(1),
            SubscriptionConstants.BillingCycles.Semiannual => newStart.AddMonths(6),
            SubscriptionConstants.BillingCycles.Yearly     => newStart.AddYears(1),
            _                                          => newStart.AddMonths(1) // default monthly
        };
        return (newStart, newEnd);
    }
}
