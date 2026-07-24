using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.BillingService.Application.Mappers;

namespace WarpTalk.BillingService.Application.Services;

public class PaymentAppService : IPaymentAppService
{
    private readonly IStripePaymentService _stripePaymentService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<PaymentAppService> _logger;
    private readonly IBillingMessagePublisher _messagePublisher;

    public PaymentAppService(
        IStripePaymentService stripePaymentService,
        IUnitOfWork unitOfWork,
        ILogger<PaymentAppService> logger,
        IBillingMessagePublisher messagePublisher)
    {
        _stripePaymentService = stripePaymentService;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _messagePublisher = messagePublisher;
    }

    public async Task<Result<string>> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest request)
    {
        try
        {
            if (request.WorkspaceId == Guid.Empty)
                return Result.Failure<string>(ErrorCodes.ValidationError, ApiMessageConstants.ValidationMessages.WorkspaceIdRequired);

            var sessionId = await _stripePaymentService.CreateCheckoutSessionAsync(request);
            return Result.Success<string>(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create checkout session");
            return Result.Failure<string>(ErrorCodes.InternalServerError, "Failed to create checkout session");
        }
    }

    public async Task<Result<CheckoutSessionDto>> GetCheckoutSessionAsync(string sessionId)
    {
        try
        {
            var session = await _stripePaymentService.GetCheckoutSessionAsync(sessionId);
            return Result.Success<CheckoutSessionDto>(session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get checkout session");
            return Result.Failure<CheckoutSessionDto>(ErrorCodes.InternalServerError, "Failed to get checkout session");
        }
    }

    public async Task<Result> ProcessPaymentEventAsync(StripePaymentEventRequest request)
    {
        try
        {
            _logger.LogInformation(BillingMessageConstants.LogMessages.ProcessPaymentEventCalled, request.StripeSessionId, request.Status);

            // Validate and parse workspace ID from metadata string
            if (!Guid.TryParse(request.WorkspaceIdStr, out Guid workspaceId))
            {
                _logger.LogError(BillingMessageConstants.LogMessages.InvalidWorkspaceIdInMetadata, request.WorkspaceIdStr);
                return Result.Failure(ErrorCodes.ValidationError, "Invalid workspace id");
            }

            // Parse user ID from metadata string (optional — may be empty)
            Guid.TryParse(request.UserIdStr, out Guid userId);

            // Resolve provider transaction ID from session or payment intent
            string providerTxId = !string.IsNullOrEmpty(request.StripeSessionId) ? request.StripeSessionId : request.PaymentIntentId;

            // Idempotent check — skip if payment already recorded as Paid
            Payment? existingPayment = await _unitOfWork.PaymentRepository.FirstOrDefaultAsync(p => p.ProviderTransactionId == providerTxId);
            if (existingPayment != null && existingPayment.Status == PaymentConstants.PaymentStatuses.Paid)
            {
                _logger.LogInformation(BillingMessageConstants.LogMessages.StripePaymentAlreadyProcessed, providerTxId);
                return Result.Success();
            }

            // Load active subscription for this workspace
            Subscription? sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null);

            string parsedPaymentStatus = request.Status.Equals("paid", StringComparison.OrdinalIgnoreCase) 
                ? PaymentConstants.PaymentStatuses.Paid 
                : request.Status.ToLower();

            // --- Handle Credit Top-Up payment type ---
            if (request.PaymentType == PaymentConstants.PaymentTypes.CreditTopUp)
            {
                // Credit top-up requires an existing active subscription — workspace must subscribe first
                if (sub == null)
                {
                    _logger.LogError(BillingMessageConstants.LogMessages.NoActiveSubscriptionForTopUp, workspaceId);
                    return Result.Failure(ErrorCodes.BillingSubscriptionNotFound, "No active subscription found for top-up");
                }

                if (parsedPaymentStatus == PaymentConstants.PaymentStatuses.Paid)
                {
                    // Calculate credits based on currency (VND is zero-decimal, others multiply by 100)
                    int creditsAdded = string.Equals(request.Currency, PaymentConstants.Currencies.Vnd, StringComparison.OrdinalIgnoreCase)
                        ? (int)request.Amount
                        : (int)(request.Amount * 100m);

                    // Top up subscription credits (EF Change Tracker auto-detects changes)
                    sub.CreditsRemaining += creditsAdded;
                    sub.UpdatedAt = DateTime.UtcNow;

                    // Create credit ledger entry for this top-up event
                    CreditTransaction topupTx = new CreditTransaction
                    {
                        Id = Guid.NewGuid(),
                        SubscriptionId = sub.Id,
                        UserId = userId,
                        Amount = creditsAdded,
                        Type = TransactionConstants.TransactionTypes.TopUp,
                        Description = BillingMessageConstants.SuccessMessages.StripeCreditTopUp,
                        ReferenceId = existingPayment?.Id ?? Guid.NewGuid(),
                        ReferenceType = TransactionConstants.ReferenceTypes.StripePayment,
                        BalanceAfter = sub.CreditsRemaining,
                        CreatedAt = DateTime.UtcNow
                    };
                    await _unitOfWork.CreditTransactionRepository.AddAsync(topupTx);
                }
            }
            // --- Handle Subscription, Subscription Renewal, or Subscription Update payment type ---
            else if (request.PaymentType == PaymentConstants.PaymentTypes.Subscription || request.PaymentType == PaymentConstants.PaymentTypes.SubscriptionRenewal || request.PaymentType == PaymentConstants.PaymentTypes.SubscriptionUpdate)
            {
                Plan? plan = await _unitOfWork.PlanRepository.FirstOrDefaultAsync(p => p.Slug.ToLower() == request.PlanSlug.ToLower() && p.DeletedAt == null);
                if (plan == null)
                {
                    _logger.LogError(BillingMessageConstants.LogMessages.PlanNotFoundForSubscription, request.PlanSlug);
                    return Result.Failure(ErrorCodes.BillingPlanNotFound, "Plan not found");
                }

                if (parsedPaymentStatus == PaymentConstants.PaymentStatuses.Paid)
                {
                    // Deactivate all other active subscriptions for this workspace
                    IReadOnlyList<Subscription> oldSubs = await _unitOfWork.SubscriptionRepository.FindAsync(
                        s => s.WorkspaceId == workspaceId && s.IsActive && s.Id != (sub != null ? sub.Id : Guid.Empty));
                    foreach (Subscription oldSub in oldSubs)
                    {
                        oldSub.AutoRenew = false;
                        oldSub.Status = SubscriptionConstants.SubscriptionStatuses.Cancelled;
                        oldSub.UpdatedAt = DateTime.UtcNow;
                        // EF Change Tracker detects property changes automatically — no .Update() needed
                    }

                    // Calculate period end based on billing cycle
                    DateTime periodEnd = request.BillingCycle.ToLower() == SubscriptionConstants.BillingCycles.Yearly
                        ? DateTime.UtcNow.AddYears(1)
                        : DateTime.UtcNow.AddMonths(1);

                    if (sub == null)
                    {
                        // Create new subscription if none exists
                        sub = new Subscription
                        {
                            Id = Guid.NewGuid(),
                            WorkspaceId = workspaceId,
                            PlanId = plan.Id,
                            UserId = userId,
                            Status = SubscriptionConstants.SubscriptionStatuses.Active,
                            CreditsRemaining = plan.CreditsPerCycle,
                            CreditsUsedThisCycle = 0,
                            CurrentPeriodStart = DateTime.UtcNow,
                            CurrentPeriodEnd = periodEnd,
                            AutoRenew = true,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        };
                        await _unitOfWork.SubscriptionRepository.AddAsync(sub);
                    }
                    else
                    {
                        // Upgrade existing subscription to new plan (EF Change Tracker auto-detects changes)
                        sub.PlanId = plan.Id;
                        sub.Status = SubscriptionConstants.SubscriptionStatuses.Active;
                        sub.IsActive = true;
                        sub.CreditsRemaining += plan.CreditsPerCycle;
                        sub.CreditsUsedThisCycle = 0;
                        sub.CurrentPeriodStart = DateTime.UtcNow;
                        sub.CurrentPeriodEnd = periodEnd;
                        sub.UpdatedAt = DateTime.UtcNow;
                    }

                    // Create credit ledger entry for this subscription activation
                    CreditTransaction topupTx = new CreditTransaction
                    {
                        Id = Guid.NewGuid(),
                        SubscriptionId = sub.Id,
                        UserId = userId,
                        Amount = plan.CreditsPerCycle,
                        Type = TransactionConstants.TransactionTypes.TopUp,
                        Description = request.PaymentType == PaymentConstants.PaymentTypes.SubscriptionUpdate 
                            ? string.Format(BillingMessageConstants.AdjustmentMessages.PlanUpgradeDirect, plan.Name)
                            : string.Format(BillingMessageConstants.SuccessMessages.SubscriptionPlanActivationTemplate, plan.Name),
                        ReferenceId = existingPayment?.Id ?? Guid.NewGuid(),
                        ReferenceType = TransactionConstants.ReferenceTypes.StripePayment,
                        BalanceAfter = sub.CreditsRemaining,
                        CreatedAt = DateTime.UtcNow
                    };
                    await _unitOfWork.CreditTransactionRepository.AddAsync(topupTx);
                }
            }
            // --- Handle Cancellation or Refund ---
            else if (request.Status == PaymentConstants.PaymentStatuses.Cancelled || request.Status == PaymentConstants.PaymentStatuses.Refunded)
            {
                if (sub != null)
                {
                    // Deactivate subscription on cancellation/refund (EF Change Tracker auto-detects changes)
                    sub.Status = SubscriptionConstants.SubscriptionStatuses.Cancelled;
                    sub.AutoRenew = false;
                    sub.UpdatedAt = DateTime.UtcNow;
                }
            }

            // Persist or update payment record
            if (existingPayment == null)
            {
                // Create new payment record
                existingPayment = new Payment
                {
                    Id = Guid.NewGuid(),
                    SubscriptionId = sub?.Id ?? Guid.Empty,
                    UserId = userId,
                    Amount = request.Amount,
                    TaxAmount = 0m,
                    TotalAmount = request.Amount,
                    Currency = request.Currency ?? PaymentConstants.Currencies.Usd,
                    PaymentMethod = PaymentConstants.PaymentMethods.Card,
                    Provider = PaymentConstants.Providers.Stripe,
                    ProviderTransactionId = providerTxId,
                    Status = parsedPaymentStatus,
                    FailureReason = request.FailureReason,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                await _unitOfWork.PaymentRepository.AddAsync(existingPayment);
            }
            else
            {
                // Update existing payment record (EF Change Tracker auto-detects changes)
                existingPayment.Status = parsedPaymentStatus;
                existingPayment.FailureReason = request.FailureReason;
                existingPayment.UpdatedAt = DateTime.UtcNow;
            }

            // Create invoice for successful payments
            if (parsedPaymentStatus == PaymentConstants.PaymentStatuses.Paid)
            {
                string invoiceNum = "INV-" + DateTime.UtcNow.ToString("yyyyMMdd") + "-" + existingPayment.Id.ToString().Substring(0, 8).ToUpper();
                Invoice invoice = new Invoice
                {
                    Id = Guid.NewGuid(),
                    PaymentId = existingPayment.Id,
                    UserId = userId,
                    InvoiceNumber = invoiceNum,
                    Subtotal = request.Amount,
                    Tax = 0,
                    Total = request.Amount,
                    Currency = request.Currency ?? PaymentConstants.Currencies.Usd,
                    Status = InvoiceConstants.InvoiceStatuses.Paid,
                    PdfUrl = request.InvoicePdf,
                    LineItems = "[]",
                    IssuedAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                };
                await _unitOfWork.InvoiceRepository.AddAsync(invoice);
            }

            // Flush all pending changes to database atomically
            await _unitOfWork.SaveChangesAsync();

            // Publish realtime update if payment was successful and subscription was changed/activated
            if (parsedPaymentStatus == PaymentConstants.PaymentStatuses.Paid && sub != null && (request.PaymentType == PaymentConstants.PaymentTypes.Subscription || request.PaymentType == PaymentConstants.PaymentTypes.SubscriptionRenewal || request.PaymentType == PaymentConstants.PaymentTypes.SubscriptionUpdate))
            {
                try
                {
                    var planName = string.Empty;
                    var plan = await _unitOfWork.PlanRepository.FirstOrDefaultAsync(p => p.Id == sub.PlanId);
                    if (plan != null)
                    {
                        planName = plan.Name;
                    }
                    
                    var action = request.PaymentType == PaymentConstants.PaymentTypes.SubscriptionUpdate ? BillingMessageConstants.Notifications.ActionChanged : BillingMessageConstants.Notifications.ActionCreated;
                    var msg = NotificationMapper.ToSubscriptionChangedMessage(userId, action, planName);
                    await _messagePublisher.PublishAsync(BillingMessageConstants.Notifications.Channel, msg, default);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to publish realtime subscription update for user {UserId}", userId);
                }
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process payment event");
            return Result.Failure(ErrorCodes.InternalServerError, "Failed to process payment event");
        }
    }
}
