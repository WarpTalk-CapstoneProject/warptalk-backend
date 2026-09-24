namespace WarpTalk.Shared.Coordination;

/// <summary>
/// The three atomic primitives a lease needs, and nothing else.
///
/// Split out from <see cref="DistributedLockProvider"/> so the lease's own rules (local validity
/// deadline, step-down on a lost renewal, token-checked release) are unit-testable against a
/// clock the test controls, while <see cref="RedisLeaseStore"/> is tested against a real Redis.
/// Every operation is compare-and-set on the caller's token: a store must never let one holder
/// renew or delete another holder's lease.
/// </summary>
public interface ILeaseStore
{
    /// <summary>
    /// Takes <paramref name="resource"/> for <paramref name="token"/> if nobody holds it.
    /// Returns the fencing token (strictly increasing per resource, across every holder that has
    /// ever acquired it) or <c>null</c> when somebody else holds the lease.
    /// </summary>
    Task<long?> TryAcquireAsync(string resource, string token, TimeSpan leaseDuration);

    /// <summary>
    /// Extends the lease to <paramref name="leaseDuration"/> from now, only if
    /// <paramref name="token"/> still holds it. <c>false</c> means the lease is gone: it expired
    /// and was possibly taken by another holder.
    /// </summary>
    Task<bool> TryRenewAsync(string resource, string token, TimeSpan leaseDuration);

    /// <summary>Deletes the lease only if <paramref name="token"/> still holds it.</summary>
    Task<bool> ReleaseAsync(string resource, string token);
}
