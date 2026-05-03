using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Services.Interface;

public interface IQuotaService
{
    Task<QuotaCheckResponse> CheckQuotaByOwnerAsync(Guid ownerUserId, CancellationToken ct = default);

    Task<QuotaTopUpResponse> TopUpQuotaByOwnerAsync(
        Guid ownerUserId,
        decimal credits,
        string? referenceId = null,
        CancellationToken ct = default);

    Task<QuotaDeductResponse> DeductAsync(
        Guid ownerUserId,
        decimal credits,
        CancellationToken ct = default);

    Task<QuotaRefundResponse> RefundAsync(
        Guid ownerUserId,
        decimal credits,
        CancellationToken ct = default);

    Task<IReadOnlyList<SubscriptionPlan>> GetPlansAsync(CancellationToken ct = default);

    Task<bool> UpgradePlanByOwnerAsync(
        Guid ownerUserId,
        Guid planId,
        CancellationToken ct = default);
}