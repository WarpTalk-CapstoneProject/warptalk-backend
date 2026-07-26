using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Helpers;
using WarpTalk.BillingService.Infrastructure.Options;

namespace WarpTalk.BillingService.Infrastructure.Workers;

public sealed class InvoiceOverdueSweeper : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<InvoiceOverdueSweeper> _logger;
    private readonly BillingWorkerOptions _options;

    public InvoiceOverdueSweeper(
        IServiceProvider serviceProvider,
        ILogger<InvoiceOverdueSweeper> logger,
        IOptions<BillingWorkerOptions> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InvoiceOverdueSweeper started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "InvoiceOverdueSweeper: error during overdue sweep.");
            }

            await Task.Delay(_options.InvoiceOverdueInterval, stoppingToken);
        }
    }

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var redisStore = scope.ServiceProvider.GetRequiredService<IRedisBillingStore>();
        var notificationClient = scope.ServiceProvider.GetService<INotificationClient>();
        var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();

        var now = DateTime.UtcNow;
        await SendDueRemindersAsync(unitOfWork, notificationClient, redis.GetDatabase(), now, cancellationToken);

        var overdueInvoices = await unitOfWork.InvoiceRepository.GetOverdueOpenInvoicesAsync(now, cancellationToken);
        var suspended = 0;

        foreach (var invoice in overdueInvoices)
        {
            var subscription = invoice.Payment.Subscription;
            var graceHours = subscription.Plan.InvoiceGraceHours;
            if (invoice.DueAt?.AddHours(graceHours) >= now)
                continue;

            if (subscription.ServiceState == SubscriptionConstants.ServiceStates.Suspended &&
                subscription.SuspendedReason == SubscriptionConstants.SuspendedReasons.InvoiceOverdue)
                continue;

            subscription.ServiceState = SubscriptionConstants.ServiceStates.Suspended;
            subscription.SuspendedReason = SubscriptionConstants.SuspendedReasons.InvoiceOverdue;
            subscription.UpdatedAt = now;
            unitOfWork.SubscriptionRepository.Update(subscription);

            await redisStore.SetAiServiceStateAsync(
                subscription.WorkspaceId,
                subscription.ServiceState,
                subscription.SuspendedReason,
                cancellationToken);
            suspended++;
        }

        if (suspended > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            _logger.LogWarning("InvoiceOverdueSweeper: suspended {Count} subscription(s) for overdue invoices.", suspended);
        }
    }

    private static async Task SendDueRemindersAsync(
        IUnitOfWork unitOfWork,
        INotificationClient? notificationClient,
        IDatabase redis,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (notificationClient is null)
            return;

        var candidates = await unitOfWork.InvoiceRepository.GetOpenInvoicesDueBeforeAsync(now.AddDays(7), cancellationToken);
        foreach (var invoice in candidates)
        {
            var kind = InvoiceReminderHelper.ResolveReminderKind(invoice.DueAt!.Value, now);
            if (kind is null)
                continue;

            var dedupeKey = $"billing:invoice_reminder:{invoice.Id}:{kind}";
            var shouldSend = await redis.StringSetAsync(dedupeKey, "sent", TimeSpan.FromDays(45), When.NotExists);
            if (!shouldSend)
                continue;

            await notificationClient.SendNotificationsAsync(
                new SendBillingNotificationsRequest(
                    new[] { invoice.UserId },
                    BillingMessageConstants.Notifications.Types.SubscriptionChanged,
                    "Billing invoice reminder",
                    $"Invoice {invoice.InvoiceNumber} is {InvoiceReminderHelper.DescribeReminder(kind)}. Total: {invoice.Total:N0} {invoice.Currency}.",
                    BillingMessageConstants.Notifications.ActionUrls.Billing,
                    new Dictionary<string, string>
                    {
                        ["invoice_id"] = invoice.Id.ToString(),
                        ["invoice_number"] = invoice.InvoiceNumber,
                        ["reminder_kind"] = kind
                    }),
                cancellationToken);
        }
    }

}
