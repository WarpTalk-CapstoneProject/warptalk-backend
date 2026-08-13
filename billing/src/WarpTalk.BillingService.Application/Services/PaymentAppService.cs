using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class PaymentAppService : IPaymentAppService
{
    private readonly IStripePaymentService _stripePaymentService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<PaymentAppService> _logger;
    private readonly IBillingMessagePublisher _messagePublisher;
    private readonly IReadOnlyList<IPaymentEventHandler> _paymentEventHandlers;
    private readonly IWorkspaceClient _workspaceClient;

    public PaymentAppService(
        IStripePaymentService stripePaymentService,
        IUnitOfWork unitOfWork,
        ILogger<PaymentAppService> logger,
        IBillingMessagePublisher messagePublisher,
        IEnumerable<IPaymentEventHandler> paymentEventHandlers,
        IWorkspaceClient workspaceClient)
    {
        _stripePaymentService = stripePaymentService;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _messagePublisher = messagePublisher;
        _paymentEventHandlers = paymentEventHandlers.ToList();
        _workspaceClient = workspaceClient;
    }

    public async Task<Result<string>> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest request)
    {
        try
        {
            if (request.WorkspaceId == Guid.Empty)
            {
                return Result.Failure<string>(
                    ApiMessageConstants.ValidationMessages.WorkspaceIdRequired,
                    ErrorCodes.ValidationError);
            }

            var result = await _stripePaymentService.CreateCheckoutSessionAsync(request);
            if (!result.IsSuccess)
            {
                // The REASON, not just the fact. This branch logged a bare "Failed to create
                // checkout session" and dropped result.Error on the floor, so a production
                // checkout that refuses every plan leaves a log line that says a checkout
                // failed and nothing about why — which is exactly the state this was found in.
                // The provider's own message is the only thing that distinguishes a missing
                // API key from a plan with no price id from a declined request.
                _logger.LogError(
                    "{Message}. WorkspaceId: {WorkspaceId}, Plan: {PlanSlug}, Cycle: {BillingCycle}, Amount: {Amount} {Currency}, Reason: {Reason} ({ErrorCode})",
                    BillingMessageConstants.LogMessages.FailedToCreateCheckoutSession,
                    request.WorkspaceId,
                    request.PlanSlug,
                    request.BillingCycle,
                    request.Amount,
                    request.Currency,
                    result.Error,
                    result.ErrorCode);
                return Result.Failure<string>(
                    result.Error ?? BillingMessageConstants.ApiErrorMessages.BillingCheckoutSessionCreateFailed,
                    ErrorCodes.InternalServerError);
            }

            return Result.Success(result.Value!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.FailedToCreateCheckoutSession);
            return Result.Failure<string>(
                BillingMessageConstants.ApiErrorMessages.BillingCheckoutSessionCreateFailed,
                ErrorCodes.InternalServerError);
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
                return Result.Failure<CheckoutSessionDto>(
                    result.Error ?? BillingMessageConstants.ApiErrorMessages.BillingCheckoutSessionGetFailed,
                    ErrorCodes.InternalServerError);
            }

            return Result.Success(result.Value!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.FailedToGetCheckoutSession);
            return Result.Failure<CheckoutSessionDto>(
                BillingMessageConstants.ApiErrorMessages.BillingCheckoutSessionGetFailed,
                ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<CheckoutSessionDto>> GetAndProcessCheckoutSessionAsync(string sessionId, Guid userId, bool isSystemAdmin)
    {
        var sessionResult = await GetCheckoutSessionAsync(sessionId);
        if (!sessionResult.IsSuccess)
        {
            return sessionResult;
        }

        var session = sessionResult.Value!;

        string workspaceIdStr = session.Metadata.GetValueOrDefault(PaymentConstants.StripeMetadata.WorkspaceId, string.Empty);
        if (!Guid.TryParse(workspaceIdStr, out Guid workspaceId))
        {
            return Result.Failure<CheckoutSessionDto>(ApiMessageConstants.ErrorMessages.BillingWorkspaceIdNotInSessionMetadata, ErrorCodes.ValidationError);
        }

        // WT-370 — THE BUYER IS AUTHORISED BY THE SESSION THEY ARE HOLDING.
        //
        // This is the path a person lands on the instant Stripe redirects them back, and it is the
        // only thing that activates their plan when the webhook does not. It used to begin with a
        // cross-service call to workspace-service on EVERY visit, and a call that fails for any
        // reason — the service restarting, a gRPC hiccup, a slow deploy — is indistinguishable
        // here from "you are not allowed", so it returned 403 and the plan was never applied.
        // A payment that succeeded should not hinge on a second service being reachable in the
        // three seconds after checkout.
        //
        // Stripe's own session metadata already names who started this checkout. If that is the
        // caller, they are the buyer — a stronger claim than "is currently an Owner of the
        // workspace", and one that needs no network at all. The role check stays for everyone
        // else (an admin opening someone else's receipt), so nothing is loosened: a stranger with
        // a guessed session id still has to pass exactly the check they had to pass before.
        var sessionUserIdStr = session.Metadata.GetValueOrDefault(PaymentConstants.StripeMetadata.UserId, string.Empty);
        var isTheBuyer = Guid.TryParse(sessionUserIdStr, out var sessionUserId) && sessionUserId == userId;

        if (!isSystemAdmin && !isTheBuyer)
        {
            var accessResult = await _workspaceClient.VerifyWorkspaceRolesAsync(
                workspaceId,
                userId,
                WorkspaceRoleConstants.Owner,
                WorkspaceRoleConstants.Admin);

            if (!accessResult.IsSuccess || !accessResult.Value)
            {
                return Result.Failure<CheckoutSessionDto>(ApiMessageConstants.ErrorMessages.BillingAccessDeniedOwnerAdminRequired, ErrorCodes.Forbidden);
            }
        }

        if (session.PaymentStatus == PaymentConstants.Payments.StatusPaid)
        {
            bool isZeroDecimal = string.Equals(session.Currency, PaymentConstants.Currencies.Vnd, StringComparison.OrdinalIgnoreCase);
            decimal finalAmount = isZeroDecimal ? (session.AmountTotal ?? 0) : ((session.AmountTotal ?? 0) / 100m);

            var processResult = await ProcessPaymentEventAsync(new StripePaymentEventRequest(
                StripeSessionId: session.Id,
                PaymentIntentId: !string.IsNullOrEmpty(session.PaymentIntentId) ? session.PaymentIntentId : string.Empty,
                Amount: finalAmount,
                Currency: session.Currency,
                UserIdStr: session.Metadata.GetValueOrDefault(PaymentConstants.StripeMetadata.UserId, string.Empty),
                WorkspaceIdStr: session.Metadata.GetValueOrDefault(PaymentConstants.StripeMetadata.WorkspaceId, string.Empty),
                PaymentType: session.Metadata.GetValueOrDefault(PaymentConstants.StripeMetadata.PaymentType, string.Empty),
                Status: PaymentConstants.Payments.StatusPaid,
                PlanSlug: session.Metadata.GetValueOrDefault(PaymentConstants.StripeMetadata.PlanSlug, string.Empty),
                BillingCycle: session.Metadata.GetValueOrDefault(PaymentConstants.StripeMetadata.BillingCycle, string.Empty)
            ));
            
            if (!processResult.IsSuccess)
            {
                // The RECEIPT still gets through. Failing the whole request here meant a payment
                // that Stripe had taken came back to the browser as a bare error: the success page
                // showed "Transaction ID: Pending / Amount: Processing…" with every field blank,
                // which is the screenshot on this ticket. The session is a fact — it is paid, it
                // has an id and an amount, and the person who just paid is entitled to see it.
                //
                // Activation is reported by the subscription itself, which the client queries
                // separately, so hiding the receipt bought nothing and cost the one piece of
                // evidence support needs. Retrying is safe and re-enters this same method:
                // ProcessPaymentEventAsync short-circuits on an already-paid payment record.
                _logger.LogError(
                    "Checkout-session activation failed for session {SessionId}, workspace {WorkspaceId}: {Error} ({ErrorCode})",
                    session.Id,
                    workspaceId,
                    processResult.Error,
                    processResult.ErrorCode);
            }
        }

        return Result.Success(session);
    }

    public async Task<Result> ProcessPaymentEventAsync(StripePaymentEventRequest request)
    {
        try
        {
            _logger.LogInformation(BillingMessageConstants.LogMessages.ProcessPaymentEventCalled, request.StripeSessionId, request.Status);

            var contextResult = await CreatePaymentEventContextAsync(request);
            if (!contextResult.IsSuccess)
            {
                return Result.Failure(contextResult.Error!, contextResult.ErrorCode);
            }

            var context = contextResult.Value!;
            if (context.ExistingPayment is { Status: PaymentConstants.PaymentStatuses.Paid })
            {
                if (context.ExistingPayment.PaidAt is null)
                {
                    context.ExistingPayment.PaidAt = DateTime.UtcNow;
                    context.ExistingPayment.UpdatedAt = DateTime.UtcNow;
                    await _unitOfWork.SaveChangesAsync();
                }

                _logger.LogInformation(BillingMessageConstants.LogMessages.StripePaymentAlreadyProcessed, context.ProviderTransactionId);
                return Result.Success();
            }

            var handler = _paymentEventHandlers.FirstOrDefault(h => h.CanHandle(context));
            if (handler is not null)
            {
                var handlerResult = await handler.HandleAsync(context);
                if (!handlerResult.IsSuccess)
                {
                    return handlerResult;
                }
            }

            await PersistPaymentRecordAsync(context);
            await CreateInvoiceForPaidPaymentAsync(context);
            await _unitOfWork.SaveChangesAsync();
            await PublishSubscriptionUpdateAsync(context);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.FailedToProcessPaymentEvent);

            // WT-370: carry WHAT went wrong, not just that something did.
            //
            // This returned a fixed sentence and dropped the exception, so the only record of the
            // real cause — a foreign-key violation, a unique-index conflict on
            // ix_subscriptions_workspace_active, a timeout — lived in a log file. Combined with
            // the webhook answering 200 (see StripeWebhookService), a failed activation left
            // literally no evidence anywhere anyone was looking.
            //
            // This message reaches exactly two places, both ours: the log, and the body of the
            // non-2xx the webhook now returns, which shows up on the Stripe delivery attempt. The
            // browser path no longer propagates it — GetAndProcessCheckoutSessionAsync logs and
            // returns the receipt instead — so no database detail reaches an end user.
            return Result.Failure(
                $"{BillingMessageConstants.ApiErrorMessages.BillingPaymentEventFailed}: {ex.GetBaseException().Message}",
                ErrorCodes.InternalServerError);
        }
    }

    private async Task<Result<PaymentEventContext>> CreatePaymentEventContextAsync(StripePaymentEventRequest request)
    {
        if (!Guid.TryParse(request.WorkspaceIdStr, out var workspaceId))
        {
            _logger.LogError(BillingMessageConstants.LogMessages.InvalidWorkspaceIdInMetadata, request.WorkspaceIdStr);
            return Result.Failure<PaymentEventContext>(
                BillingMessageConstants.ApiErrorMessages.BillingWorkspaceIdInvalid,
                ErrorCodes.ValidationError);
        }

        Guid.TryParse(request.UserIdStr, out var userId);

        var providerTxId = !string.IsNullOrEmpty(request.StripeSessionId)
            ? request.StripeSessionId
            : request.PaymentIntentId;

        var existingPayment = await _unitOfWork.PaymentRepository.FirstOrDefaultAsync(p => p.ProviderTransactionId == providerTxId);
        var subscription = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null);

        return Result.Success(request.ToPaymentEventContext(
            workspaceId,
            userId,
            providerTxId,
            ParsePaymentStatus(request.Status),
            existingPayment,
            subscription));
    }

    private async Task PersistPaymentRecordAsync(PaymentEventContext context)
    {
        if (context.ExistingPayment is null)
        {
            var payment = PaymentMapper.CreateStripePayment(new StripePaymentCreationRequest(
                SubscriptionId: context.Subscription?.Id,
                UserId: context.UserId,
                Amount: context.Request.Amount,
                Currency: context.Request.Currency,
                ProviderTransactionId: context.ProviderTransactionId,
                Status: context.ParsedPaymentStatus,
                FailureReason: context.Request.FailureReason));

            payment.Id = context.PaymentId;
            await _unitOfWork.PaymentRepository.AddAsync(payment);
            context.ExistingPayment = payment;
            return;
        }

        context.ExistingPayment.Status = context.ParsedPaymentStatus;
        context.ExistingPayment.FailureReason = context.Request.FailureReason;
        if (context.ParsedPaymentStatus == PaymentConstants.PaymentStatuses.Paid)
        {
            context.ExistingPayment.PaidAt ??= DateTime.UtcNow;
        }
        context.ExistingPayment.UpdatedAt = DateTime.UtcNow;
    }

    private async Task CreateInvoiceForPaidPaymentAsync(PaymentEventContext context)
    {
        if (context.ParsedPaymentStatus != PaymentConstants.PaymentStatuses.Paid || context.ExistingPayment is null)
        {
            return;
        }

        var existingInvoice = await _unitOfWork.InvoiceRepository.FirstOrDefaultAsync(
            i => i.PaymentId == context.ExistingPayment.Id);

        if (existingInvoice is not null)
        {
            existingInvoice.Status = InvoiceConstants.InvoiceStatuses.Paid;
            existingInvoice.PaidAt = DateTime.UtcNow;
            existingInvoice.PdfUrl = string.IsNullOrWhiteSpace(context.Request.InvoicePdf)
                ? existingInvoice.PdfUrl
                : context.Request.InvoicePdf;
            return;
        }

        var invoice = InvoiceMapper.CreateStripeInvoice(new StripeInvoiceCreationRequest(
            PaymentId: context.ExistingPayment.Id,
            UserId: context.UserId,
            Amount: context.Request.Amount,
            Currency: context.Request.Currency,
            PdfUrl: context.Request.InvoicePdf));

        await _unitOfWork.InvoiceRepository.AddAsync(invoice);
    }

    private async Task PublishSubscriptionUpdateAsync(PaymentEventContext context)
    {
        if (!context.SubscriptionChanged || context.Subscription is null)
        {
            return;
        }

        try
        {
            var planName = string.Empty;
            var plan = await _unitOfWork.Plans.FirstOrDefaultAsync(p => p.Id == context.Subscription.PlanId);
            if (plan != null)
            {
                planName = plan.Name;
            }

            var action = context.Request.PaymentType == PaymentConstants.PaymentTypes.SubscriptionUpdate
                ? BillingMessageConstants.Notifications.ActionChanged
                : BillingMessageConstants.Notifications.ActionCreated;
            var message = NotificationMapper.ToSubscriptionChangedMessage(context.UserId, action, planName);
            await _messagePublisher.PublishAsync(BillingMessageConstants.Notifications.Channel, message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, BillingMessageConstants.LogMessages.FailedToPublishRealtimeSubscriptionUpdate, context.UserId);
        }
    }

    private static string ParsePaymentStatus(string status)
        => status.Equals(PaymentConstants.PaymentStatuses.Paid, StringComparison.OrdinalIgnoreCase)
            ? PaymentConstants.PaymentStatuses.Paid
            : status.ToLowerInvariant();
}
