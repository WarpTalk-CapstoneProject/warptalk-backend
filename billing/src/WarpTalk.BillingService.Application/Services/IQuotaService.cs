using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Services;

public interface IQuotaService
{
    Task<QuotaCheckResponse> CheckQuotaAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<QuotaDeductResponse> DeductQuotaAsync(Guid workspaceId, QuotaDeductRequest request, CancellationToken cancellationToken = default);
    Task<QuotaDeductResponse> RefundQuotaAsync(Guid workspaceId, QuotaRefundRequest request, CancellationToken cancellationToken = default);
    Task<IEnumerable<SubscriptionPlan>> GetAvailablePlansAsync(CancellationToken cancellationToken = default);

    Task<bool> UpgradePlanAsync(Guid workspaceId, Guid planId, CancellationToken cancellationToken = default);
}
