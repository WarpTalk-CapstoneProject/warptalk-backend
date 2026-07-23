using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;
using NotificationClient = WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient;

namespace WarpTalk.BillingService.Application.Services;

public class CreditService : ICreditService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CreditService> _logger;
    private readonly IBillingMessagePublisher _messagePublisher;
    private readonly IConfiguration _configuration;
    private readonly INotificationClient? _notificationClient;

    public CreditService(
        IUnitOfWork unitOfWork,
        ILogger<CreditService> logger,
        IBillingMessagePublisher messagePublisher,
        IConfiguration configuration,
        INotificationClient? notificationClient = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _messagePublisher = messagePublisher;
        _configuration = configuration;
        _notificationClient = notificationClient;
    }

    private async Task<Result<Subscription>> GetActiveSubscriptionAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var sub = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(workspaceId, cancellationToken: cancellationToken);
        if (sub is null)
            return Result.Failure<Subscription>(
                ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                ErrorCodes.BillingSubscriptionNotFound);

        return Result.Success(sub);
    }

    public async Task<Result<CreditBalanceDto>> GetWorkspaceCreditsAsync(
        Guid workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var subResult = await GetActiveSubscriptionAsync(workspaceId, cancellationToken);
            if (!subResult.IsSuccess)
                return Result.Failure<CreditBalanceDto>(subResult.Error, subResult.ErrorCode);
            var sub = subResult.Value;

            return Result.Success(sub.ToCreditBalanceDto(workspaceId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting workspace credits for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<CreditBalanceDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public Task<Result<CreditTransactionDto>> ConsumeCreditsDirectlyAsync(
        Guid workspaceId, ConsumeCreditsRequest request, CancellationToken cancellationToken = default)
    {
        return ConcurrencyRetryHelper.ExecuteWithConcurrencyRetryAsync(_unitOfWork, _logger, workspaceId, async () =>
        {
            if (request.Amount <= 0)
                return Result.Failure<CreditTransactionDto>(ApiMessageConstants.ErrorMessages.BillingInvalidAmount, ErrorCodes.BillingInvalidAmount);

            var subResult = await GetActiveSubscriptionAsync(workspaceId, cancellationToken);
            if (!subResult.IsSuccess)
                return Result.Failure<CreditTransactionDto>(subResult.Error, subResult.ErrorCode);
            var sub = subResult.Value;

            if (sub.CreditsRemaining < request.Amount)
                return Result.Failure<CreditTransactionDto>(ApiMessageConstants.ErrorMessages.BillingInsufficientCredits, ErrorCodes.BillingInsufficientCredits);

            sub.CreditsRemaining -= request.Amount;
            sub.CreditsUsedThisCycle += request.Amount;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            var transaction = request.ToEntity(sub);
            await _unitOfWork.CreditTransactionRepository.AddAsync(transaction, cancellationToken);

            var usage = request.ToUsageRecord(sub);
            await _unitOfWork.UsageRecordRepository.AddAsync(usage, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await PublishCreditUpdateAsync(
                NotificationMapper.ToCreditsUpdatedMessage(sub.UserId, sub.CreditsRemaining, string.Empty, string.Empty),
                cancellationToken);

            return Result.Success(transaction.ToDto());
        }, cancellationToken);
    }

    public Task<Result<CreditBalanceDto>> TopUpCreditsAsync(
        Guid workspaceId, TopUpRequest request, CancellationToken cancellationToken = default)
    {
        return ConcurrencyRetryHelper.ExecuteWithConcurrencyRetryAsync(_unitOfWork, _logger, workspaceId, async () =>
        {
            if (request.Amount <= 0)
                return Result.Failure<CreditBalanceDto>(ApiMessageConstants.ErrorMessages.BillingInvalidAmount, ErrorCodes.BillingInvalidAmount);

            var subResult = await GetActiveSubscriptionAsync(workspaceId, cancellationToken);
            if (!subResult.IsSuccess)
                return Result.Failure<CreditBalanceDto>(subResult.Error, subResult.ErrorCode);
            var sub = subResult.Value;

            sub.CreditsRemaining += request.Amount;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            var transaction = request.ToEntity(sub);
            var paymentId = Guid.NewGuid();
            transaction.ReferenceId = paymentId;
            transaction.ReferenceType = BillingConstants.ReferenceTypes.StripePayment;

            await _unitOfWork.CreditTransactionRepository.AddAsync(transaction, cancellationToken);

            decimal credits = request.Amount;
            decimal estimatedCostUsd = credits * 0.01m; // 1 USD = 100 Credits (1 Credit = $0.01 USD)

            // Create Payment record
            var paymentTx = request.ToEntity(sub, paymentId, estimatedCostUsd, BillingConstants.Currencies.Usd);
            await _unitOfWork.PaymentRepository.AddAsync(paymentTx, cancellationToken);

            // Create Invoice record
            var invoice = request.ToEntity(paymentTx);
            await _unitOfWork.InvoiceRepository.AddAsync(invoice, cancellationToken);

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await PublishCreditUpdateAsync(
                NotificationMapper.ToCreditsUpdatedMessage(
                    sub.UserId,
                    sub.CreditsRemaining,
                    BillingConstants.SuccessMessages.CreditsAddedTitle,
                    string.Format(BillingConstants.SuccessMessages.CreditsAddedContent, request.Amount)),
                cancellationToken);

            return Result.Success(sub.ToCreditBalanceDto(workspaceId));
        }, cancellationToken);
    }

    // TODO: This method is for internal testing/simulation only and does NOT integrate with real Stripe.
    // - stripeInvoiceId is a randomly generated mock ID, not a real Stripe invoice ID.
    // - The invoice PdfUrl generated by ToSimulatedEntity() is a non-functional placeholder.
    // When real Stripe integration is ready, this method should be replaced by the actual
    // Stripe Checkout or Payment Intent flow, and PdfUrl should be populated from the
    // Stripe Webhook event payload (invoice.payment_succeeded → invoice_pdf field).
    public async Task<Result<object>> SimulatePaymentAsync(Guid workspaceId, decimal amount, string currency, CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
            return Result.Failure<object>(ApiMessageConstants.ErrorMessages.BillingInvalidAmount, ErrorCodes.BillingInvalidAmount);

        var subResult = await GetActiveSubscriptionAsync(workspaceId, cancellationToken);
        if (!subResult.IsSuccess)
            return Result.Failure<object>(subResult.Error, subResult.ErrorCode);
        var sub = subResult.Value;

        var paymentId = Guid.NewGuid();
        // TODO: Mock ID only — not a real Stripe invoice ID. Replace with actual Stripe invoice ID from webhook.
        var stripeInvoiceId = "in_" + Guid.NewGuid().ToString("N")[..14];

        var paymentTx = sub.ToSimulatedEntity(paymentId, stripeInvoiceId, amount, currency);
        await _unitOfWork.PaymentRepository.AddAsync(paymentTx, cancellationToken);

        var invoice = paymentTx.ToSimulatedEntity();
        await _unitOfWork.InvoiceRepository.AddAsync(invoice, cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success<object>(new { message = BillingConstants.SuccessMessages.SimulatePaymentMessage, invoiceId = invoice.Id, stripeInvoiceId });
    }

    public async Task<Result<PaginatedResponse<CreditTransactionDto>>> GetCreditHistoryAsync(
        Guid workspaceId,
        CreditHistoryQuery query,
        CancellationToken cancellationToken = default)
    {   //all subscriptions of workspace
        var subs = await _unitOfWork.SubscriptionRepository.FindAsync(
            s => s.WorkspaceId == workspaceId && s.DeletedAt == null,
            cancellationToken);

        var subIds = subs.Select(s => s.Id).ToList();
        if (subIds.Count == 0)
            return Result.Failure<PaginatedResponse<CreditTransactionDto>>(
                ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                ErrorCodes.BillingSubscriptionNotFound);

        // page size min is 1 and max is 200
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        // skip = (page number - 1) * page size
        var skip = (Math.Max(1, query.PageNumber) - 1) * pageSize;
        //base query: join Subscription, filter to this workspace's subscription IDs only
        IQueryable<CreditTransaction> baseQuery = _unitOfWork.CreditTransactionRepository.Query()
            .Include(t => t.Subscription)
            .Where(t => subIds.Contains(t.SubscriptionId));
        // apply filters
        var filteredQuery = ApplyCreditTransactionFilters(baseQuery, query);
        // count total 
        var total = await filteredQuery.CountAsync(cancellationToken);
        // take page
        var items = await filteredQuery
            .OrderByDescending(t => t.CreatedAt)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        // create paginated response
        return Result.Success(PaginatedResponse<CreditTransactionDto>.Create(
            items.ToDtoList(workspaceId), total, query.PageNumber, pageSize));
    }

    public async Task<Result<PaginatedResponse<CreditTransactionDto>>> GetGlobalCreditHistoryAsync(
        CreditHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var skip = (Math.Max(1, query.PageNumber) - 1) * pageSize;

        IQueryable<CreditTransaction> baseQuery = _unitOfWork.CreditTransactionRepository.Query()
            .Include(t => t.Subscription);

        // filter for workspaceId
        if (query.WorkspaceId.HasValue)
        {
            var targetSub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == query.WorkspaceId.Value && s.DeletedAt == null,
                cancellationToken);
            if (targetSub != null)
                baseQuery = baseQuery.Where(t => t.SubscriptionId == targetSub.Id);
        }

        var filteredQuery = ApplyCreditTransactionFilters(baseQuery, query);

        var total = await filteredQuery.CountAsync(cancellationToken);
        var items = await filteredQuery
            .OrderByDescending(t => t.CreatedAt)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var dtos = items.ToDtoList();

        try
        {
            var workspaceIds = dtos
                .Where(d => d.WorkspaceId.HasValue && d.WorkspaceId != Guid.Empty)
                .Select(d => d.WorkspaceId!.Value)
                .Distinct()
                .ToArray();

            if (workspaceIds.Length > 0)
            {
                var workspaceNames = await _unitOfWork.CreditTransactionRepository.GetWorkspaceNamesAsync(workspaceIds, cancellationToken);

                dtos = dtos.Select(d =>
                    d.WorkspaceId.HasValue && workspaceNames.TryGetValue(d.WorkspaceId.Value, out var wName)
                        ? d with { WorkspaceName = wName }
                        : d
                ).ToList();
            }
        }
        catch (Exception wsEx)
        {
            _logger.LogWarning(wsEx, BillingConstants.LogMessages.FailedToResolveWorkspaceNames);
        }

        return Result.Success(PaginatedResponse<CreditTransactionDto>.Create(dtos, total, query.PageNumber, pageSize));
    }

    public Task<Result<CreditTransactionDto>> ManualAdjustCreditsAsync(
        Guid workspaceId,
        ManualAdjustCreditsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Amount == 0)
            return Task.FromResult(Result.Failure<CreditTransactionDto>(ApiMessageConstants.ErrorMessages.BillingInvalidAmount, ErrorCodes.BillingInvalidAmount));
        if (string.IsNullOrWhiteSpace(request.AdminUserId))
            return Task.FromResult(Result.Failure<CreditTransactionDto>(ApiMessageConstants.ErrorMessages.BillingAccessDenied, ErrorCodes.Forbidden));

        return ConcurrencyRetryHelper.ExecuteWithConcurrencyRetryAsync(_unitOfWork, _logger, workspaceId, async () =>
        {
            var subResult = await GetActiveSubscriptionAsync(workspaceId, cancellationToken);
            if (!subResult.IsSuccess)
                return Result.Failure<CreditTransactionDto>(subResult.Error, subResult.ErrorCode);
            var sub = subResult.Value;

            sub.CreditsRemaining += request.Amount;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            var adjustmentTransaction = request.ToEntity(sub);

            await _unitOfWork.CreditTransactionRepository.AddAsync(adjustmentTransaction, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var notificationTitle = request.Amount > 0
                ? BillingConstants.AdjustmentMessages.AddedTitle
                : BillingConstants.AdjustmentMessages.DeductedTitle;

            var notificationContent = string.Format(
                BillingConstants.AdjustmentMessages.ContentTemplate,
                request.Amount > 0 ? "+" : "",
                request.Amount,
                adjustmentTransaction.Description);

            await PublishCreditUpdateAsync(
                NotificationMapper.ToCreditsUpdatedMessage(sub.UserId, sub.CreditsRemaining, notificationTitle, notificationContent),
                cancellationToken);

            return Result.Success(adjustmentTransaction.ToDto());
        }, cancellationToken);
    }

    private async Task PublishCreditUpdateAsync(WarpTalk.Shared.Models.RealtimeNotificationMessage msg, CancellationToken cancellationToken)
    {
        try
        {
            await _messagePublisher.PublishAsync(BillingConstants.Notifications.Channel, msg, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, BillingConstants.LogMessages.FailedToPublishRealtimeCreditUpdate, msg.UserId);
        }
    }

    // Shared filter logic for both workspace credit history and global credit history queries.
    // Each condition is applied independently — only added to the query if the client provides a value.
    private static IQueryable<CreditTransaction> ApplyCreditTransactionFilters(
        IQueryable<CreditTransaction> query, CreditHistoryQuery filter)
    {
        if (!string.IsNullOrEmpty(filter.Type))
            query = query.Where(t => t.Type == filter.Type);

        if (filter.FromDate.HasValue)
            query = query.Where(t => t.CreatedAt >= filter.FromDate.Value);

        if (filter.ToDate.HasValue)
            query = query.Where(t => t.CreatedAt <= filter.ToDate.Value);

        if (filter.MinAmount.HasValue)
            query = query.Where(t => Math.Abs(t.Amount) >= filter.MinAmount.Value);

        if (filter.MaxAmount.HasValue)
            query = query.Where(t => Math.Abs(t.Amount) <= filter.MaxAmount.Value);

        return query;
    }
}
