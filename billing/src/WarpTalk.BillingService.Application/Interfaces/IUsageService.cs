using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IUsageService
{

    Task<Result<CreditBalanceDto>> RecordUsageAsync(RecordUsageRequest request, CancellationToken cancellationToken = default);
    Task<Result<CreditBalanceDto>> ChargeVoiceCloneAsync(ChargeVoiceCloneRequest request, CancellationToken cancellationToken = default);
    Task<Result<CreditBalanceDto>> ChargeAiAssistantAsync(ChargeAiAssistantRequest request, CancellationToken cancellationToken = default);
    Task<Result<CreditBalanceDto>> ChargeDocumentTranslationAsync(ChargeDocumentTranslationRequest request, CancellationToken cancellationToken = default);
    Task<Result<bool>> LogUsageOnlyAsync(RecordUsageRequest request, CancellationToken cancellationToken = default);
}
