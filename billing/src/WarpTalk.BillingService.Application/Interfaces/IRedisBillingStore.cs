using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.DTOs;

public interface IRedisBillingStore
{
    Task SetReservationAsync(RedisCreditReservationDto reservation, TimeSpan ttl, CancellationToken cancellationToken = default);
    Task<RedisCreditReservationDto?> GetAndRemoveReservationAsync(string idempotencyKey, CancellationToken cancellationToken = default);
    Task<IEnumerable<RedisCreditReservationDto>> GetExpiredReservationsAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    Task RemoveReservationAsync(string idempotencyKey, CancellationToken cancellationToken = default);

    Task SetSessionActiveAsync(Guid sessionId, TimeSpan ttl, CancellationToken cancellationToken = default);
    Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<IEnumerable<Guid>> GetExpiredSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    Task RemoveSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task PushTempUsageLogDtoAsync(TempUsageLogDto log, CancellationToken cancellationToken = default);
    Task<IEnumerable<TempUsageLogDto>> GetAndClearTempUsageLogDtosAsync(CancellationToken cancellationToken = default);
}
