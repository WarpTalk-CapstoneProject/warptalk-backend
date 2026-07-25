using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface ICreditGrantService
{
    Task<Result<CreditBalanceDto>> GrantCreditsAsync(Guid workspaceId, TopUpRequest request, CancellationToken cancellationToken = default);

    Task<Result<CreditTransactionDto>> QueueCreditGrantAsync(
        Subscription subscription,
        GrantCreditsRequest request,
        CancellationToken cancellationToken = default);
}
