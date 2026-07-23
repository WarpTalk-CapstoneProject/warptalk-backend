using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IRealtimeSessionBillingService
{
    Task<Result<Guid>> StartSessionAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<Result<bool>> ProcessHeartbeatAsync(Guid sessionId, Guid workspaceId, CancellationToken cancellationToken = default);
    Task<Result<CreditReservationDto>> ReserveCreditsAsync(ReserveCreditsRequest request, CancellationToken cancellationToken = default);
    Task<Result<CreditTransactionDto>> ConfirmConsumeAsync(Guid workspaceId, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<Result<bool>> RefundReservationAsync(Guid workspaceId, string idempotencyKey, CancellationToken cancellationToken = default);
}
