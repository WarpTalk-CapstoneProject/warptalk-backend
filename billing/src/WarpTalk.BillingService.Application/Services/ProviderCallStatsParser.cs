using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Services;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// Reads one day of the AI workers' provider-call hash into hourly rows. The layout is a cross-repo
/// contract with warptalk-ai shared/provider_calls.py (pinned by tests on both sides):
/// <code>
///   key    warptalk:provider_calls:{YYYY-MM-DD}             (UTC day)
///   field  {provider}|{HH}|{operation}|{model}|{outcome}    count
///          {provider}|{HH}|{operation}|{model}|lat:{le}     latency bucket count (not cumulative)
///          {provider}|{HH}|{operation}|{model}|lat_sum      Σ latency, ms
/// </code>
/// A field that does not fit is skipped, never fatal: a new outcome added on the Python side must
/// not stop the sync of every other number.
/// </summary>
public static class ProviderCallStatsParser
{
    public const string KeyPrefix = "warptalk:provider_calls:";

    public static string KeyFor(DateOnly utcDay) => KeyPrefix + utcDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The outcomes a field may carry — warptalk-ai PROVIDER_CALL_OUTCOMES plus Stripe's "declined".</summary>
    public static readonly IReadOnlySet<string> Outcomes = new HashSet<string>(StringComparer.Ordinal)
    {
        "ok", "quota", "rate_limited", "auth", "client_error", "server_error", "timeout", "network_error", "error", "declined",
    };

    /// <summary>warptalk-ai PROVIDER_LATENCY_BUCKETS_MS — the two writers must bucket alike.</summary>
    public static readonly IReadOnlyList<int> LatencyBucketsMs = [100, 250, 500, 1000, 2000, 3000, 5000, 8000, 12000, 20000];

    /// <summary>The (field, increment) pairs one call adds to its day's hash — the writer's half of <see cref="Parse"/>.</summary>
    public static IReadOnlyList<(string Field, long Increment)> FieldsFor(
        string provider, string operation, string? model, string outcome, long? latencyMs, DateTime atUtc)
    {
        static string Label(string? value)
        {
            var cleaned = System.Text.RegularExpressions.Regex.Replace((value ?? string.Empty).Trim(), @"[|\s]+", "-");
            if (cleaned.Length > 80) cleaned = cleaned[..80];
            return cleaned.Length == 0 ? "-" : cleaned;
        }

        var prefix = string.Join('|', Label(provider).ToLowerInvariant(), atUtc.Hour.ToString("00", CultureInfo.InvariantCulture), Label(operation), Label(model));
        var fields = new List<(string, long)> { ($"{prefix}|{(Outcomes.Contains(outcome) ? outcome : "error")}", 1) };
        if (latencyMs is { } ms && ms >= 0)
        {
            var edge = LatencyBucketsMs.FirstOrDefault(e => ms <= e);
            fields.Add(($"{prefix}|lat:{(edge == 0 ? "+Inf" : edge.ToString(CultureInfo.InvariantCulture))}", 1));
            fields.Add(($"{prefix}|lat_sum", ms));
        }

        return fields;
    }

    public static IReadOnlyList<ProviderCallStat> Parse(DateOnly utcDay, IEnumerable<KeyValuePair<string, long>> fields)
    {
        var rows = new Dictionary<(string Provider, int Hour, string Operation, string Model), (ProviderCallStat Row, Dictionary<string, long> Buckets)>();

        foreach (var (field, value) in fields)
        {
            var parts = field.Split('|');
            if (parts.Length != 5 || value < 0) continue;
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var hour) || hour is < 0 or > 23) continue;
            var provider = parts[0].Trim().ToLowerInvariant();
            if (provider.Length == 0 || provider.Length > 40) continue;
            var operation = Clip(parts[2], 80);
            var model = Clip(parts[3], 120);

            var key = (provider, hour, operation, model);
            if (!rows.TryGetValue(key, out var entry))
            {
                entry = (new ProviderCallStat
                {
                    Provider = provider,
                    HourStart = utcDay.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Utc),
                    Operation = operation,
                    Model = model,
                }, new Dictionary<string, long>(StringComparer.Ordinal));
                rows[key] = entry;
            }

            var row = entry.Row;
            var suffix = parts[4];
            switch (suffix)
            {
                case "ok": row.Ok += value; break;
                case "quota": row.Quota += value; break;
                case "rate_limited": row.RateLimited += value; break;
                case "auth": row.Auth += value; break;
                case "client_error": row.ClientError += value; break;
                case "server_error": row.ServerError += value; break;
                case "timeout": row.Timeout += value; break;
                case "network_error": row.NetworkError += value; break;
                case "error": row.Error += value; break;
                case "declined": row.Declined += value; break;
                case "lat_sum": row.LatencySumMs += value; break;
                default:
                    if (suffix.StartsWith("lat:", StringComparison.Ordinal) && suffix.Length > 4)
                    {
                        var edge = suffix[4..];
                        entry.Buckets[edge] = entry.Buckets.GetValueOrDefault(edge) + value;
                    }

                    break;
            }
        }

        return rows.Values
            .Select(entry =>
            {
                entry.Row.LatencyCount = entry.Buckets.Values.Sum();
                entry.Row.LatencyBuckets = ProviderCallStatMerge.SerializeBuckets(entry.Buckets);
                return entry.Row;
            })
            .OrderBy(row => row.HourStart)
            .ThenBy(row => row.Provider, StringComparer.Ordinal)
            .ThenBy(row => row.Operation, StringComparer.Ordinal)
            .ThenBy(row => row.Model, StringComparer.Ordinal)
            .ToList();
    }

    private static string Clip(string value, int max)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return "-";
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
