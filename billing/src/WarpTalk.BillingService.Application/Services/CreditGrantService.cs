using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public sealed class CreditGrantService : ICreditGrantService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CreditGrantService> _logger;
    private readonly IBillingMessagePublisher _messagePublisher;

    public CreditGrantService(
        IUnitOfWork unitOfWork,
        ILogger<CreditGrantService> logger,
        IBillingMessagePublisher messagePublisher)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _messagePublisher = messagePublisher;
    }

    public Task<Result<CreditBalanceDto>> GrantCreditsAsync(
        Guid workspaceId,
        TopUpRequest request,
        CancellationToken cancellationToken = default)
    {
        return ConcurrencyRetryHelper.ExecuteWithConcurrencyRetryAsync(_unitOfWork, _logger, workspaceId, async () =>
        {
            var subResult = await SubscriptionHelper.GetActiveSubscriptionAsync(_unitOfWork, workspaceId, cancellationToken);
            if (!subResult.IsSuccess)
            {
                return Result.Failure<CreditBalanceDto>(subResult.Error, subResult.ErrorCode);
            }

            var grantResult = await QueueCreditGrantAsync(
                subResult.Value,
                request.ToGrantCreditsRequest(subResult.Value.UserId),
                cancellationToken);
            if (!grantResult.IsSuccess)
            {
                return Result.Failure<CreditBalanceDto>(grantResult.Error, grantResult.ErrorCode);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var sub = subResult.Value;
            await BillingNotificationHelper.PublishCreditUpdateAsync(
                _messagePublisher,
                _logger,
                NotificationMapper.ToCreditsUpdatedMessage(
                    sub.UserId,
                    sub.CreditsRemaining,
                    BillingMessageConstants.SuccessMessages.CreditsAddedTitle,
                    string.Format(BillingMessageConstants.SuccessMessages.CreditsAddedContent, request.Amount)),
                cancellationToken);

            return Result.Success(sub.ToCreditBalanceDto(workspaceId));
        }, cancellationToken);
    }

    public async Task<Result<CreditTransactionDto>> QueueCreditGrantAsync(
        Subscription subscription,
        GrantCreditsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Amount <= 0)
        {
            return Result.Failure<CreditTransactionDto>(
                ApiMessageConstants.ErrorMessages.BillingInvalidAmount,
                ErrorCodes.BillingInvalidAmount);
        }

        subscription.ApplyGrant(request.Amount);
        _unitOfWork.SubscriptionRepository.Update(subscription);

        var transaction = request.ToEntity(subscription);
        await _unitOfWork.CreditTransactionRepository.AddAsync(transaction, cancellationToken);
        return Result.Success(transaction.ToDto(request.WorkspaceId));
    }
}
