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

    public PaymentAppService(
        IStripePaymentService stripePaymentService,
        IUnitOfWork unitOfWork,
        ILogger<PaymentAppService> logger,
        IBillingMessagePublisher messagePublisher,
        IEnumerable<IPaymentEventHandler> paymentEventHandlers)
    {
        _stripePaymentService = stripePaymentService;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _messagePublisher = messagePublisher;
        _paymentEventHandlers = paymentEventHandlers.ToList();
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
                _logger.LogError(BillingMessageConstants.LogMessages.FailedToCreateCheckoutSession);
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
            return Result.Failure(
                BillingMessageConstants.ApiErrorMessages.BillingPaymentEventFailed,
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

        return Result.Success(new PaymentEventContext(
            request,
            workspaceId,
            userId,
            providerTxId,
            ParsePaymentStatus(request.Status),
            existingPayment?.Id ?? Guid.NewGuid(),
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
        context.ExistingPayment.UpdatedAt = DateTime.UtcNow;
    }

    private async Task CreateInvoiceForPaidPaymentAsync(PaymentEventContext context)
    {
        if (context.ParsedPaymentStatus != PaymentConstants.PaymentStatuses.Paid || context.ExistingPayment is null)
        {
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
            var plan = await _unitOfWork.PlanRepository.FirstOrDefaultAsync(p => p.Id == context.Subscription.PlanId);
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
