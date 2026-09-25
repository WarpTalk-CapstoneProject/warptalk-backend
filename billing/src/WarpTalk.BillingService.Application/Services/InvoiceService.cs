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
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Application.Services;

public class InvoiceService : IInvoiceService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<InvoiceService> _logger;
    private readonly IStripePaymentService _stripePaymentService;
    private readonly IWorkspaceClient _workspaceClient;

    public InvoiceService(
        IUnitOfWork unitOfWork,
        ILogger<InvoiceService> logger,
        IStripePaymentService stripePaymentService,
        IWorkspaceClient workspaceClient)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _stripePaymentService = stripePaymentService;
        _workspaceClient = workspaceClient;
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
        GlobalInvoiceQuery query, CancellationToken cancellationToken = default)
    {
        var status = string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim().ToLowerInvariant();
        if (status == "all") status = null;
        if (status != null && !InvoiceConstants.InvoiceStatuses.Filterable.Contains(status))
        {
            return Result.Failure<PaginatedResponse<InvoiceDto>>(
                InvoiceConstants.Errors.UnknownStatusFilter, ErrorCodes.ValidationError);
        }

        if (!AdminSort.TryResolve(query.Sort, GlobalInvoiceSorts.All, GlobalInvoiceSorts.IssuedDesc, out var sort))
        {
            return Result.Failure<PaginatedResponse<InvoiceDto>>(
                InvoiceConstants.Errors.UnknownSort, ErrorCodes.ValidationError);
        }

        var currency = string.IsNullOrWhiteSpace(query.Currency) ? null : query.Currency.Trim().ToUpperInvariant();
        if (currency != null && (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper)))
        {
            return Result.Failure<PaginatedResponse<InvoiceDto>>(
                InvoiceConstants.Errors.InvalidCurrency, ErrorCodes.ValidationError);
        }

        if (AdminDateFilter.ValidateRange(query.FromDate, query.ToDate, "fromDate", "toDate") is { } rangeError)
        {
            return Result.Failure<PaginatedResponse<InvoiceDto>>(rangeError, ErrorCodes.ValidationError);
        }

        if (query.MinTotal is { } min && query.MaxTotal is { } max && min > max)
        {
            return Result.Failure<PaginatedResponse<InvoiceDto>>(
                InvoiceConstants.Errors.InvalidTotalRange, ErrorCodes.ValidationError);
        }

        try
        {
            var page = await _unitOfWork.InvoiceRepository.GetGlobalPageAsync(
                new GlobalInvoiceFilter(
                    BillingQueryHelper.ToPageRequest(query),
                    string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(),
                    status,
                    query.WorkspaceId,
                    currency,
                    AdminDateFilter.ToUtc(query.FromDate),
                    AdminDateFilter.ToUtc(query.ToDate),
                    query.MinTotal,
                    query.MaxTotal,
                    sort),
                cancellationToken);

            var dtos = page.Items.Select(i => i.ToDto(i.Payment.Subscription.WorkspaceId)).ToList();

            // Resolve workspace names via workspace-service (billing must not read workspace schema)
            try
            {
                var workspaceIds = BillingQueryHelper.GetWorkspaceIds(page.Items, i => i.Payment.Subscription.WorkspaceId);
                if (workspaceIds.Length > 0)
                {
                    var namesResult = await _workspaceClient.GetWorkspaceNamesAsync(workspaceIds, cancellationToken);
                    if (namesResult.IsSuccess)
                        dtos = BillingQueryHelper.ApplyWorkspaceNames(dtos, namesResult.Value!, i => Guid.TryParse(i.WorkspaceId, out var wId) ? wId : (Guid?)null, (i, name) => i with { WorkspaceName = name });
                    else
                        _logger.LogWarning(BillingMessageConstants.LogMessages.FailedToResolveWorkspaceNamesGlobalInvoices);
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
        InvoiceCheckoutCaller caller,
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

            // WT-260, the last instance. This action authorised off [Authorize(Roles = Owner…)],
            // and a workspace Owner is membership data, never a token claim — so no real Owner
            // could ever pay, and any platform-role holder could pay (and so re-point) ANY
            // workspace's invoice. The filter cannot help: the only route value is an invoice id,
            // which it would take for a workspace id. The invoice names its workspace, so the check
            // happens here, after the lookup.
            //
            // Owner only. Paying is a spend decision, and an invoice is not a plan change an Admin
            // was already trusted with.
            var workspaceId = invoice.Payment.Subscription.WorkspaceId;
            if (!caller.IsPlatformAdmin)
            {
                var access = await _workspaceClient.VerifyWorkspaceRolesAsync(
                    workspaceId,
                    caller.UserId,
                    WorkspaceRoleConstants.Owner);

                if (!access.IsSuccess)
                {
                    return Result.Failure<string>(
                        access.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError,
                        ErrorCodes.InternalServerError);
                }

                if (!access.Value)
                {
                    return Result.Failure<string>(
                        ApiMessageConstants.ErrorMessages.BillingAccessDenied,
                        ErrorCodes.Forbidden);
                }
            }

            if (invoice.Status == InvoiceConstants.InvoiceStatuses.Paid || invoice.PaidAt is not null)
            {
                return Result.Failure<string>(
                    BillingMessageConstants.ApiErrorMessages.BillingInvoiceAlreadyPaid,
                    ErrorCodes.ValidationError);
            }

            // A voided or written-off invoice is not owed. Taking money for one would be a charge
            // with nothing behind it, and nothing downstream would reverse it.
            if (invoice.Status is InvoiceConstants.InvoiceStatuses.Void
                or InvoiceConstants.InvoiceStatuses.Uncollectible)
            {
                return Result.Failure<string>(
                    BillingMessageConstants.ApiErrorMessages.BillingInvoiceNotPayable,
                    ErrorCodes.ValidationError);
            }

            var subscription = invoice.Payment.Subscription;
            var plan = subscription.Plan;
            var checkoutResult = await _stripePaymentService.CreateCheckoutSessionAsync(
                // WT-545: the buyer is whoever is paying now, not whoever the invoice was raised
                // to. GetAndProcessCheckoutSessionAsync lets the session's named buyer through
                // without a role check, so it has to be the person holding this token.
                new CreateCheckoutSessionRequest(
                    UserId: caller.UserId,
                    WorkspaceId: subscription.WorkspaceId,
                    Amount: invoice.Total,
                    Currency: invoice.Currency,
                    PaymentType: PaymentConstants.PaymentTypes.InvoicePayment,
                    PlanSlug: plan?.Slug ?? SubscriptionConstants.PlanSlugs.Enterprise,
                    BillingCycle: plan?.BillingCycle ?? SubscriptionConstants.BillingCycles.Monthly,
                    BuyerEmail: caller.Email),
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

            invoice.MarkPaid(DateTime.UtcNow);
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
