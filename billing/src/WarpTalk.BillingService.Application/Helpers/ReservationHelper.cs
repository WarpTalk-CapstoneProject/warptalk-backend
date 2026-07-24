using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Helpers;

public record ReservationLookupRequest(
    IRedisBillingStore RedisStore,
    ILogger Logger,
    string IdempotencyKey,
    string TransactionType
);

public static class ReservationHelper
{
    public static async Task<(CreditTransaction? existingTx, RedisCreditReservationDto? reservation)> ValidateAndGetReservationAsync(
        ReservationLookupRequest request,
        CancellationToken cancellationToken)
    {
        var reservationResult = await request.RedisStore.GetAndRemoveReservationAsync(request.IdempotencyKey, cancellationToken);
        if (!reservationResult.IsSuccess)
        {
            request.Logger.LogWarning(BillingMessageConstants.LogMessages.FailedToPublishRealtimeCreditUpdateForWorkspace, reservationResult.Error);
            return (null, null);
        }
        return (null, reservationResult.Value);
    }
}
