using System;
using System.Globalization;
using System.Threading.Tasks;
using StackExchange.Redis;
using WarpTalk.TranscriptService.Application.DTOs;

namespace WarpTalk.TranscriptService.Application.Services;

/// <summary>
/// PO 2026-10-02 (onboarding "Set Up Glossary → Load Knowledge Into WarpBot"): whether a
/// glossary's terms have actually reached WarpBot's knowledge.
///
/// THE PATH THAT ALREADY EXISTED: every added/imported/edited term is published to the
/// <c>embedding:index_requests</c> stream (GlossaryService.TryPublishEmbeddingIndexRequestAsync,
/// source_type <c>glossary_term</c>); warptalk-ai's EmbeddingWorker embeds it into the workspace's
/// <c>workspace_{id}</c> collection, which WarpBot's semantic search reads, and answers on
/// <c>embedding:index_results</c> with status indexed / failed / blocked. Nobody read the
/// glossary results, so nothing could say whether it worked.
///
/// THE MINIMUM HONEST STATUS ADDED: per glossary, a Redis hash of how many term index requests
/// were sent and how many results came back of each kind, plus the set of terms whose LATEST
/// result was a failure. The publisher remembers job → glossary + term (<see cref="JobKey"/>),
/// the GlossaryIndexResultConsumer counts results only for jobs it knows, so replaying old
/// results cannot inflate a count. Seven-day TTL: older than that the glossary reads as "idle"
/// (nothing in flight), which is the truth once its results are long settled. No schema change.
/// </summary>
public static class GlossaryWarpBotStatus
{
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    /// <summary>
    /// Results normally arrive within seconds. Requests still unanswered this long after the last
    /// movement are reported as "stalled" rather than as a spinner that never ends.
    /// </summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(10);

    public const string Requested = "requested";
    public const string Indexed = "indexed";
    public const string Failed = "failed";
    public const string Blocked = "blocked";
    private const string UpdatedAtMs = "updated_at_ms";

    public static string HashKey(Guid glossaryId) => $"glossary:warpbot:{glossaryId}";

    /// <summary>Ids of the terms whose latest index result was "failed".</summary>
    public static string FailedTermsKey(Guid glossaryId) => $"glossary:warpbot:{glossaryId}:failed-terms";

    public static string JobKey(string jobId) => $"glossary:warpbot:job:{jobId}";

    /// <summary>Called right BEFORE an index request for one term is published.</summary>
    public static async Task RecordRequestedAsync(IDatabase db, Guid glossaryId, Guid termId, string jobId)
    {
        var key = HashKey(glossaryId);

        // A batch whose results never came (worker down, result trimmed) must not hold every later
        // import in "stalled": once it is past StallAfter those answers are not coming, so the
        // counters start again with this request. The failed-terms set is kept — it is per term.
        var counters = Parse(await db.HashGetAllAsync(key));
        if (IsStalled(counters, DateTime.UtcNow))
            await db.KeyDeleteAsync(key);

        await db.StringSetAsync(JobKey(jobId), $"{glossaryId}|{termId}", Ttl);
        await db.HashIncrementAsync(key, Requested);
        await TouchAsync(db, key);
    }

    /// <summary>Undoes <see cref="RecordRequestedAsync"/> for a request that was never published.</summary>
    public static async Task ForgetRequestAsync(IDatabase db, Guid glossaryId, string jobId)
    {
        try
        {
            await db.KeyDeleteAsync(JobKey(jobId));
            await db.HashDecrementAsync(HashKey(glossaryId), Requested);
        }
        catch
        {
            // Redis is what just failed; the count then over-states by one until it stalls out.
        }
    }

    /// <summary>A deleted term can no longer be "failing to load".</summary>
    public static Task ForgetTermAsync(IDatabase db, Guid glossaryId, Guid termId) =>
        db.SetRemoveAsync(FailedTermsKey(glossaryId), termId.ToString());

    /// <summary>A deleted glossary has no status.</summary>
    public static Task ForgetGlossaryAsync(IDatabase db, Guid glossaryId) =>
        db.KeyDeleteAsync(new RedisKey[] { HashKey(glossaryId), FailedTermsKey(glossaryId) });

    /// <summary>
    /// Counts one result. Returns false when the job is not one this service published (an old
    /// result, a delete, another producer) — nothing is counted then.
    /// </summary>
    public static async Task<bool> RecordResultAsync(IDatabase db, string jobId, string status)
    {
        var field = status.ToLowerInvariant() switch
        {
            "indexed" => Indexed,
            "failed" => Failed,
            "blocked" => Blocked,
            _ => null,
        };
        if (field is null) return false;

        // GET then DEL rather than GETDEL, which needs Redis 6.2.
        var job = await db.StringGetAsync(JobKey(jobId));
        if (job.IsNullOrEmpty) return false;
        await db.KeyDeleteAsync(JobKey(jobId));

        var parts = job.ToString().Split('|');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var glossaryId)) return false;

        var key = HashKey(glossaryId);
        await db.HashIncrementAsync(key, field);
        await TouchAsync(db, key);

        // "Failed" describes the terms failing NOW: a term that failed and was then indexed on a
        // later edit / re-import is no longer a failure.
        var failedTerms = FailedTermsKey(glossaryId);
        if (field == Failed)
        {
            await db.SetAddAsync(failedTerms, parts[1]);
            await db.KeyExpireAsync(failedTerms, Ttl);
        }
        else
        {
            await db.SetRemoveAsync(failedTerms, parts[1]);
        }
        return true;
    }

    public static async Task<GlossaryWarpBotStatusDto> ReadAsync(IDatabase db, Guid glossaryId)
    {
        var counters = Parse(await db.HashGetAllAsync(HashKey(glossaryId)));
        var failingTerms = await db.SetLengthAsync(FailedTermsKey(glossaryId));
        return Compute(counters, failingTerms, DateTime.UtcNow);
    }

    /// <summary>
    /// idle    nothing sent in the last 7 days (or never) — no claim either way;
    /// loading results still outstanding and still moving;
    /// stalled results still outstanding, nothing has moved for <see cref="StallAfter"/>;
    /// failed  every result is in and at least one term's latest result is a failure;
    /// ready   every result is in and no term is failing (a blocked term — inactive / not allowed
    ///         for AI — is deliberately not loaded and does not count as a failure).
    /// </summary>
    public static GlossaryWarpBotStatusDto Compute(Counters counters, long failingTerms, DateTime utcNow)
    {
        var pending = Math.Max(0, counters.Requested - counters.Settled);
        var state = counters.Requested <= 0 ? "idle"
            : IsStalled(counters, utcNow) ? "stalled"
            : pending > 0 ? "loading"
            : failingTerms > 0 ? "failed"
            : "ready";
        return new GlossaryWarpBotStatusDto(
            state, counters.Requested, counters.Indexed, Math.Max(0, failingTerms), counters.Blocked, pending, counters.UpdatedAt);
    }

    private static bool IsStalled(Counters counters, DateTime utcNow) =>
        counters.Requested > counters.Settled
        && counters.UpdatedAt is { } updatedAt
        && utcNow - updatedAt > StallAfter;

    private static async Task TouchAsync(IDatabase db, string key)
    {
        await db.HashSetAsync(key, UpdatedAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        await db.KeyExpireAsync(key, Ttl);
    }

    private static Counters Parse(HashEntry[]? entries)
    {
        long requested = 0, indexed = 0, failed = 0, blocked = 0;
        DateTime? updatedAt = null;
        foreach (var entry in entries ?? Array.Empty<HashEntry>())
        {
            if (!long.TryParse(entry.Value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) continue;
            switch (entry.Name.ToString())
            {
                case Requested: requested = value; break;
                case Indexed: indexed = value; break;
                case Failed: failed = value; break;
                case Blocked: blocked = value; break;
                case UpdatedAtMs: updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime; break;
            }
        }
        return new Counters(requested, indexed, failed, blocked, updatedAt);
    }

    /// <summary>Raw counters; FailedResults counts failure RESULTS (for settling), not failing terms.</summary>
    public readonly record struct Counters(long Requested, long Indexed, long FailedResults, long Blocked, DateTime? UpdatedAt)
    {
        public long Settled => Indexed + FailedResults + Blocked;
    }
}
