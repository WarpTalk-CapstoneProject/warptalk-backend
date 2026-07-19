using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.Interfaces;

public class RedisCreditReservation
{
    public string IdempotencyKey { get; set; } = null!;
    public Guid SubscriptionId { get; set; }
    public Guid WorkspaceId { get; set; }
    public int Amount { get; set; }
}

public interface IRedisBillingStore
{
    Task SetReservationAsync(RedisCreditReservation reservation, TimeSpan ttl, CancellationToken cancellationToken = default);
    Task<RedisCreditReservation?> GetAndRemoveReservationAsync(string idempotencyKey, CancellationToken cancellationToken = default);
    Task<IEnumerable<RedisCreditReservation>> GetExpiredReservationsAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    Task RemoveReservationAsync(string idempotencyKey, CancellationToken cancellationToken = default);


    Task SetSessionActiveAsync(Guid sessionId, TimeSpan ttl, CancellationToken cancellationToken = default);
    Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<IEnumerable<Guid>> GetExpiredSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    Task RemoveSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
