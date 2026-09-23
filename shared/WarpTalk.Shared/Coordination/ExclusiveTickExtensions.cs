using Microsoft.Extensions.Logging;

namespace WarpTalk.Shared.Coordination;

/// <summary>What happened to one attempt at an exclusive tick.</summary>
public enum ExclusiveTickOutcome
{
    /// <summary>This replica held the lease and the work ran to completion.</summary>
    Ran,

    /// <summary>Another replica holds the lease; nothing ran here.</summary>
    Skipped,

    /// <summary>
    /// The lease could not be taken or was lost mid-run (store unreachable, renewal refused);
    /// the work was not started or was cancelled.
    /// </summary>
    LeaseUnavailable,
}

/// <summary>
/// "Run this tick only if I hold the lock" for periodic workers that must not run on two replicas
/// at once. Every replica keeps its own timer; whichever takes the lease runs the tick and the
/// rest skip it.
/// </summary>
public static class ExclusiveTickExtensions
{
    /// <summary>
    /// Runs <paramref name="work"/> while holding <paramref name="resource"/>.
    ///
    /// The lease is renewed every third of <paramref name="leaseDuration"/> for as long as the work
    /// runs, so a tick is not limited to one lease duration. If a renewal is refused, or the
    /// store stays unreachable until the local deadline passes, the token handed to
    /// <paramref name="work"/> is cancelled: another replica may be about to take over, and the
    /// work must stop rather than overlap with it. Work that ignores the token can still overlap
    /// in that case, so a tick whose writes are not idempotent should pass the token to every
    /// database call.
    ///
    /// <paramref name="holdAfterCompletion"/>: when true the lease is NOT released at the end and
    /// is left to expire — "at most once per <paramref name="leaseDuration"/> cluster-wide", for
    /// work that must not run again as soon as another replica's timer fires. When false it is
    /// released, so the next replica's tick may run right away (for work that is idempotent run
    /// sequentially and only unsafe run concurrently).
    ///
    /// Never throws for a lease problem — an unreachable Redis skips the tick and logs, because an
    /// exception escaping a BackgroundService stops the whole host. Exceptions from
    /// <paramref name="work"/> itself propagate unchanged to the caller's existing handling.
    /// </summary>
    public static async Task<ExclusiveTickOutcome> TryRunExclusiveAsync(
        this IDistributedLockProvider locks,
        string resource,
        TimeSpan leaseDuration,
        Func<CancellationToken, Task> work,
        ILogger logger,
        CancellationToken cancellationToken,
        bool holdAfterCompletion = false)
    {
        IDistributedLease? lease;
        try
        {
            lease = await locks.TryAcquireAsync(resource, leaseDuration, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not reach the lock store for {Resource}; skipping this tick rather than risk running it on two replicas.",
                resource);
            return ExclusiveTickOutcome.LeaseUnavailable;
        }

        if (lease is null)
        {
            logger.LogDebug("Lease {Resource} is held by another replica; skipping this tick.", resource);
            return ExclusiveTickOutcome.Skipped;
        }

        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stopRenewing = new CancellationTokenSource();
        var renewal = KeepRenewingAsync(lease, leaseDuration, leaseLost, logger, stopRenewing.Token);
        var released = false;

        try
        {
            await work(leaseLost.Token);
            if (!holdAfterCompletion)
            {
                released = true;
            }

            return ExclusiveTickOutcome.Ran;
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            released = true;
            logger.LogWarning(
                "Lost lease {Resource} (fence {Fence}) mid-tick; the tick was cancelled so it cannot overlap with the next holder.",
                resource,
                lease.FencingToken);
            return ExclusiveTickOutcome.LeaseUnavailable;
        }
        catch
        {
            // A failed tick hands the lease back so another replica can retry it on its next beat.
            released = true;
            throw;
        }
        finally
        {
            await stopRenewing.CancelAsync();
            try
            {
                await renewal;
            }
            catch
            {
                // KeepRenewingAsync never throws; belt and braces.
            }

            if (released)
            {
                await lease.DisposeAsync();
            }
        }
    }

    private static async Task KeepRenewingAsync(
        IDistributedLease lease,
        TimeSpan leaseDuration,
        CancellationTokenSource leaseLost,
        ILogger logger,
        CancellationToken stop)
    {
        var interval = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(10).Ticks, leaseDuration.Ticks / 3));
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (!await lease.TryRenewAsync(stop))
                {
                    logger.LogWarning("Lease {Resource} was refused on renewal; cancelling the running tick.", lease.Resource);
                    await leaseLost.CancelAsync();
                    return;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!lease.IsHeld)
                {
                    logger.LogWarning(ex, "Lease {Resource} could not be renewed before its deadline; cancelling the running tick.", lease.Resource);
                    await leaseLost.CancelAsync();
                    return;
                }

                logger.LogDebug(ex, "Renewing lease {Resource} failed; still within its deadline, will retry.", lease.Resource);
            }
        }
    }
}
