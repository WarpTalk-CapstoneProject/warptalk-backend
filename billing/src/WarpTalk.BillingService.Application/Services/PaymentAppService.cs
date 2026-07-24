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

            var result = await _stripePaymentService.CreateCheckoutSessionAsync(request);
            if (!result.IsSuccess)
            {
                _logger.LogError(BillingMessageConstants.LogMessages.FailedToCreateCheckoutSession);
                return Result.Failure<string>(result.Error ?? BillingMessageConstants.ApiErrorMessages.BillingCheckoutSessionCreateFailed, ErrorCodes.InternalServerError);
            }
            return Result.Success<string>(result.Value!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.FailedToCreateCheckoutSession);
            return Result.Failure<string>(ErrorCodes.InternalServerError, BillingMessageConstants.ApiErrorMessages.BillingCheckoutSessionCreateFailed);
        }
    }

    public async Task<Result<CheckoutSessionDto>> GetCheckoutSessionAsync(string sessionId)
    {
        try
        {
            var result = await _stripePaymentService.GetCheckoutSessionAsync(sessionId);
            if (!result.IsSuccess)
            {
                _logger.LogError(BillingMessageConstants.LogMessages.FailedToGetCheckoutSession);
                return Result.Failure<CheckoutSessionDto>(result.Error ?? BillingMessageConstants.ApiErrorMessages.BillingCheckoutSessionGetFailed, ErrorCodes.InternalServerError);
            }
            return Result.Success<CheckoutSessionDto>(result.Value!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.FailedToGetCheckoutSession);
            return Result.Failure<CheckoutSessionDto>(ErrorCodes.InternalServerError, BillingMessageConstants.ApiErrorMessages.BillingCheckoutSessionGetFailed);
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
                return Result.Failure(ErrorCodes.ValidationError, BillingMessageConstants.ApiErrorMessages.BillingWorkspaceIdInvalid);
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
                    return Result.Failure(ErrorCodes.BillingSubscriptionNotFound, BillingMessageConstants.ApiErrorMessages.BillingTopUpSubscriptionNotFound);
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
                    CreditTransaction topupTx = sub.CreateStripeTopUpTransaction(creditsAdded, userId, existingPayment?.Id ?? Guid.NewGuid());
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
                    return Result.Failure(ErrorCodes.BillingPlanNotFound, ApiMessageConstants.ErrorMessages.BillingPlanNotFound);
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
                        sub = SubscriptionMapper.CreateNewStripeSubscription(workspaceId, userId, plan, periodEnd);
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
                    CreditTransaction topupTx = CreditMapper.CreateStripeSubscriptionTransaction(
                        new StripeSubscriptionTransactionRequest(
                            sub,
                            plan,
                            request.PaymentType,
                            userId,
                            existingPayment?.Id ?? Guid.NewGuid()));
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
                existingPayment = PaymentMapper.CreateStripePayment(new StripePaymentCreationRequest(
                    SubscriptionId: sub?.Id,
                    UserId: userId,
                    Amount: request.Amount,
                    Currency: request.Currency,
                    ProviderTransactionId: providerTxId,
                    Status: parsedPaymentStatus,
                    FailureReason: request.FailureReason));
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
                Invoice invoice = InvoiceMapper.CreateStripeInvoice(new StripeInvoiceCreationRequest(
                    PaymentId: existingPayment.Id,
                    UserId: userId,
                    Amount: request.Amount,
                    Currency: request.Currency,
                    PdfUrl: request.InvoicePdf));
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
                    _logger.LogWarning(ex, BillingMessageConstants.LogMessages.FailedToPublishRealtimeSubscriptionUpdate, userId);
                }
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.FailedToProcessPaymentEvent);
            return Result.Failure(ErrorCodes.InternalServerError, BillingMessageConstants.ApiErrorMessages.BillingPaymentEventFailed);
        }
    }
}
