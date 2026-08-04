using System;
using WarpTalk.BillingService.Domain.Constants;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class InvoiceService : IInvoiceService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<InvoiceService> _logger;
    private readonly IStripePaymentService _stripePaymentService;

    public InvoiceService(
        IUnitOfWork unitOfWork,
        ILogger<InvoiceService> logger,
        IStripePaymentService stripePaymentService)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _stripePaymentService = stripePaymentService;
    }

    public async Task<Result<PaginatedResponse<InvoiceDto>>> GetInvoicesAsync(
        Guid workspaceId, PaginationQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await _unitOfWork.InvoiceRepository.GetPageAsync(
                BillingQueryHelper.ToPageRequest(query),
                workspaceId,
                cancellationToken);

            var dtos = page.Items.Select(i => i.ToDto(workspaceId)).ToList();
            return Result.Success(PaginatedResponse<InvoiceDto>.Create(dtos, page.TotalCount, page.PageNumber, page.PageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingInvoices, workspaceId);
            return Result.Failure<PaginatedResponse<InvoiceDto>>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PaginatedResponse<InvoiceDto>>> GetGlobalInvoicesAsync(
        PaginationQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await _unitOfWork.InvoiceRepository.GetPageAsync(
                BillingQueryHelper.ToPageRequest(query),
                null,
                cancellationToken);

            var dtos = page.Items.Select(i => i.ToDto(i.Payment.Subscription.WorkspaceId)).ToList();

            // Resolve workspace names cross-schema
            try
            {
                var workspaceIds = BillingQueryHelper.GetWorkspaceIds(page.Items, i => i.Payment.Subscription.WorkspaceId);
                if (workspaceIds.Length > 0)
                {
                    var workspaceNames = await _unitOfWork.CreditTransactionRepository.GetWorkspaceNamesAsync(workspaceIds, cancellationToken);
                    dtos = BillingQueryHelper.ApplyWorkspaceNames(dtos, workspaceNames, i => Guid.TryParse(i.WorkspaceId, out var wId) ? wId : (Guid?)null, (i, name) => i with { WorkspaceName = name });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, BillingMessageConstants.LogMessages.FailedToResolveWorkspaceNamesGlobalInvoices);
            }

            return Result.Success(PaginatedResponse<InvoiceDto>.Create(dtos, page.TotalCount, page.PageNumber, page.PageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingGlobalInvoices);
            return Result.Failure<PaginatedResponse<InvoiceDto>>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<string>> CreateInvoiceCheckoutSessionAsync(
        Guid invoiceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var invoice = await _unitOfWork.InvoiceRepository.FirstOrDefaultAsync(
                i => i.Id == invoiceId,
                "Payment.Subscription.Plan",
                cancellationToken);

            if (invoice is null)
            {
                return Result.Failure<string>(
                    BillingMessageConstants.ApiErrorMessages.BillingInvoiceNotFound,
                    ErrorCodes.NotFound);
            }

            if (invoice.Status == InvoiceConstants.InvoiceStatuses.Paid)
            {
                return Result.Failure<string>(
                    BillingMessageConstants.ApiErrorMessages.BillingInvoiceAlreadyPaid,
                    ErrorCodes.ValidationError);
            }

            var subscription = invoice.Payment.Subscription;
            var plan = subscription.Plan;
            var checkoutResult = await _stripePaymentService.CreateCheckoutSessionAsync(
                new CreateCheckoutSessionRequest(
                    UserId: invoice.UserId,
                    WorkspaceId: subscription.WorkspaceId,
                    Amount: invoice.Total,
                    Currency: invoice.Currency,
                    PaymentType: PaymentConstants.PaymentTypes.InvoicePayment,
                    PlanSlug: plan?.Slug ?? SubscriptionConstants.PlanSlugs.Enterprise,
                    BillingCycle: plan?.BillingCycle ?? SubscriptionConstants.BillingCycles.Monthly),
                cancellationToken);

            if (!checkoutResult.IsSuccess)
            {
                return Result.Failure<string>(
                    checkoutResult.Error ?? BillingMessageConstants.ApiErrorMessages.BillingCheckoutSessionCreateFailed,
                    checkoutResult.ErrorCode);
            }

            invoice.Payment.Provider = PaymentConstants.Providers.Stripe;
            invoice.Payment.PaymentMethod = PaymentConstants.PaymentMethods.Card;
            invoice.Payment.ProviderTransactionId = ExtractCheckoutSessionId(checkoutResult.Value!);
            invoice.Payment.UpdatedAt = DateTime.UtcNow;
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(checkoutResult.Value!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.FailedToCreateCheckoutSession);
            return Result.Failure<string>(
                BillingMessageConstants.ApiErrorMessages.BillingCheckoutSessionCreateFailed,
                ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<InvoiceDto>> MarkInvoicePaidAsync(
        Guid invoiceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var invoice = await _unitOfWork.InvoiceRepository.FirstOrDefaultAsync(
                i => i.Id == invoiceId,
                "Payment.Subscription",
                cancellationToken);

            if (invoice is null)
            {
                return Result.Failure<InvoiceDto>(
                    BillingMessageConstants.ApiErrorMessages.BillingInvoiceNotFound,
                    ErrorCodes.NotFound);
            }

            if (invoice.Status == InvoiceConstants.InvoiceStatuses.Paid)
            {
                return Result.Success(invoice.ToDto(invoice.Payment.Subscription.WorkspaceId));
            }

            var paidAt = DateTime.UtcNow;
            invoice.Status = InvoiceConstants.InvoiceStatuses.Paid;
            invoice.PaidAt = paidAt;
            invoice.Payment.Status = PaymentConstants.PaymentStatuses.Paid;
            invoice.Payment.PaidAt = paidAt;
            invoice.Payment.UpdatedAt = DateTime.UtcNow;
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(invoice.ToDto(invoice.Payment.Subscription.WorkspaceId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorUpdatingPaymentStatus, invoiceId);
            return Result.Failure<InvoiceDto>(
                ApiMessageConstants.ErrorMessages.BillingInternalError,
                ErrorCodes.InternalServerError);
        }
    }

    private static string ExtractCheckoutSessionId(string checkoutUrl)
    {
        var sessionIndex = checkoutUrl.IndexOf("cs_", StringComparison.OrdinalIgnoreCase);
        if (sessionIndex < 0)
        {
            return checkoutUrl;
        }

        var endIndex = checkoutUrl.IndexOfAny(new[] { '?', '&', '#', '/' }, sessionIndex);
        return endIndex > sessionIndex
            ? checkoutUrl[sessionIndex..endIndex]
            : checkoutUrl[sessionIndex..];
    }
}
