using System;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// One UTC hour of calls WarpTalk made to one provider operation and model
/// (subscription.provider_call_stats), copied from the AI workers' Redis hashes
/// (<c>warptalk:provider_calls:{day}</c>, warptalk-ai shared/provider_calls.py) by
/// ProviderCallStatsSyncWorker. What OUR calls saw — not the vendor's status page.
///
/// Counters only grow: a sync takes the larger of the stored and the reported value, so a Redis
/// hash lost to eviction can undercount an hour but never erase it.
/// </summary>
public sealed class ProviderCallStat
{
    public Guid Id { get; set; }

    public string Provider { get; set; } = string.Empty;

    /// <summary>Start of the UTC hour.</summary>
    public DateTime HourStart { get; set; }

    public string Operation { get; set; } = string.Empty;

    /// <summary>The model the request named; <c>-</c> when unknown.</summary>
    public string Model { get; set; } = string.Empty;

    public long Ok { get; set; }
    public long Quota { get; set; }
    public long RateLimited { get; set; }
    public long Auth { get; set; }
    public long ClientError { get; set; }
    public long ServerError { get; set; }
    public long Timeout { get; set; }
    public long NetworkError { get; set; }
    public long Error { get; set; }

    /// <summary>Stripe 402 card_error: the card was declined. Neither a success nor a provider failure.</summary>
    public long Declined { get; set; }

    /// <summary>Calls with a latency observation, and Σ of it in ms.</summary>
    public long LatencyCount { get; set; }
    public long LatencySumMs { get; set; }

    /// <summary>Histogram as JSON <c>{"100":3,"250":9,…,"+Inf":0}</c> (not cumulative).</summary>
    public string LatencyBuckets { get; set; } = "{}";

    public DateTime SyncedAt { get; set; }
}
