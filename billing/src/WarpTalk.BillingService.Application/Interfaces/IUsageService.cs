using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IUsageService
{
    int CalculateCreditCost(int audioSeconds, int tokenCount, int gpuInferenceMs, bool isVoiceClone, Plan plan);

    Task<Result<CreditBalanceDto>> RecordUsageAsync(RecordUsageRequest request, CancellationToken cancellationToken = default);
    Task<Result<bool>> LogUsageOnlyAsync(RecordUsageRequest request, CancellationToken cancellationToken = default);
}
