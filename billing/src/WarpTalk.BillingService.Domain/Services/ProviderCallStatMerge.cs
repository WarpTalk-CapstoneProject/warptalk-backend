using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Services;

/// <summary>
/// How a re-read of the AI workers' hourly counters lands on a stored row. Every counter is
/// cumulative for its hour, so the newer reading is normally the larger; taking the MAX (not the
/// latest) means a Redis hash lost to eviction and restarted from zero can only undercount the
/// hour, never wipe what was already copied.
/// </summary>
public static class ProviderCallStatMerge
{
    /// <summary>Raises every counter of <paramref name="target"/> to at least <paramref name="reading"/>'s. True when anything changed.</summary>
    public static bool RaiseTo(ProviderCallStat target, ProviderCallStat reading)
    {
        var changed = false;
        long Max(long current, long next)
        {
            if (next > current) { changed = true; return next; }
            return current;
        }

        target.Ok = Max(target.Ok, reading.Ok);
        target.Quota = Max(target.Quota, reading.Quota);
        target.RateLimited = Max(target.RateLimited, reading.RateLimited);
        target.Auth = Max(target.Auth, reading.Auth);
        target.ClientError = Max(target.ClientError, reading.ClientError);
        target.ServerError = Max(target.ServerError, reading.ServerError);
        target.Timeout = Max(target.Timeout, reading.Timeout);
        target.NetworkError = Max(target.NetworkError, reading.NetworkError);
        target.Error = Max(target.Error, reading.Error);
        target.Declined = Max(target.Declined, reading.Declined);

        // Latency moves as one reading: count, sum and buckets from the same snapshot, or the
        // average and percentiles would mix two of them.
        if (reading.LatencyCount > target.LatencyCount)
        {
            target.LatencyCount = reading.LatencyCount;
            target.LatencySumMs = reading.LatencySumMs;
            target.LatencyBuckets = reading.LatencyBuckets;
            changed = true;
        }

        return changed;
    }

    public static ProviderCallStat Copy(ProviderCallStat row) => new()
    {
        Id = row.Id,
        Provider = row.Provider,
        HourStart = row.HourStart,
        Operation = row.Operation,
        Model = row.Model,
        Ok = row.Ok,
        Quota = row.Quota,
        RateLimited = row.RateLimited,
        Auth = row.Auth,
        ClientError = row.ClientError,
        ServerError = row.ServerError,
        Timeout = row.Timeout,
        NetworkError = row.NetworkError,
        Error = row.Error,
        Declined = row.Declined,
        LatencyCount = row.LatencyCount,
        LatencySumMs = row.LatencySumMs,
        LatencyBuckets = row.LatencyBuckets,
        SyncedAt = row.SyncedAt,
    };

    /// <summary>Calls that count against the provider's availability: everything but ok and client_error (a request we got wrong).</summary>
    public static long Failures(ProviderCallStat row)
        => row.Quota + row.RateLimited + row.Auth + row.ServerError + row.Timeout + row.NetworkError + row.Error;

    /// <summary>Every call, including client errors and declined cards.</summary>
    public static long Calls(ProviderCallStat row) => row.Ok + row.ClientError + row.Declined + Failures(row);

    /// <summary>Parses the stored histogram; a malformed value is an empty one, never an exception.</summary>
    public static IReadOnlyDictionary<string, long> ParseBuckets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, long>();
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, long>>(json);
            return parsed ?? new Dictionary<string, long>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, long>();
        }
    }

    public static string SerializeBuckets(IReadOnlyDictionary<string, long> buckets)
        => JsonSerializer.Serialize(buckets.OrderBy(pair => EdgeOf(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));

    /// <summary>The upper edge of a bucket label in ms; <c>+Inf</c> is <see cref="double.PositiveInfinity"/>.</summary>
    public static double EdgeOf(string label)
        => double.TryParse(label, NumberStyles.Float, CultureInfo.InvariantCulture, out var edge) ? edge : double.PositiveInfinity;
}
