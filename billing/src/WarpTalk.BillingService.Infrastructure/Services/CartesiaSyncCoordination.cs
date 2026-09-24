using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;

namespace WarpTalk.BillingService.Infrastructure.Services;

/// <summary>One billing replica's turn to sync Cartesia usage. <see cref="Backfill"/>: read 35 days, not 3.</summary>
public sealed record CartesiaSyncTurn(string Token, bool Backfill);

/// <summary>
/// Decides which billing replica syncs Cartesia usage, and when the next sync may run.
///
/// Billing runs 2–5 replicas under the Kubernetes chart (HPA). Without this, every replica would call
/// Cartesia on its own timer — N× the requests against an admin API whose rate limit is undocumented,
/// and N independent backoffs that each restart at the short interval. With it, the replicas share ONE
/// schedule: a turn is a lease that the replica holding it extends to "the next sync is due", so the
/// lease expiring IS the schedule, a 429 backoff applies to every replica, and whichever replica polls
/// first after it expires takes the next turn. A replica that dies mid-turn loses its lease after
/// <see cref="TurnTimeout"/>, and another takes over.
/// </summary>
public interface ICartesiaSyncCoordinator
{
    /// <summary>Null when another replica holds the turn, or the next sync is not due yet.</summary>
    Task<CartesiaSyncTurn?> TryBeginTurnAsync(TimeSpan backfillEvery, CancellationToken ct);

    /// <summary>Keep the turn until the next sync is due; record a completed backfill.</summary>
    Task EndTurnAsync(CartesiaSyncTurn turn, TimeSpan nextSyncIn, bool backfilled, TimeSpan backfillEvery, CancellationToken ct);
}

/// <summary>Redis lease: SET NX PX to take a turn, a token-checked PEXPIRE to hold it until the next sync.</summary>
public sealed class RedisCartesiaSyncCoordinator : ICartesiaSyncCoordinator
{
    public const string TurnKey = "billing:cartesia-usage-sync:turn";
    public const string BackfillKey = "billing:cartesia-usage-sync:backfilled";

    /// <summary>Longer than one sync can take: three requests at a 30 s timeout, and one database write.</summary>
    public static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(5);

    private readonly IConnectionMultiplexer _redis;
    private readonly TimeProvider _time;

    public RedisCartesiaSyncCoordinator(IConnectionMultiplexer redis, TimeProvider? time = null)
    {
        _redis = redis;
        _time = time ?? TimeProvider.System;
    }

    public async Task<CartesiaSyncTurn?> TryBeginTurnAsync(TimeSpan backfillEvery, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var token = Guid.NewGuid().ToString("N");
        if (!await db.LockTakeAsync(TurnKey, token, TurnTimeout)) return null;

        var backfilled = await db.KeyExistsAsync(BackfillKey);
        return new CartesiaSyncTurn(token, Backfill: !backfilled);
    }

    public async Task EndTurnAsync(
        CartesiaSyncTurn turn, TimeSpan nextSyncIn, bool backfilled, TimeSpan backfillEvery, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        if (backfilled)
        {
            await db.StringSetAsync(BackfillKey, _time.GetUtcNow().ToString("O"), backfillEvery);
        }

        // Only our own lease: if this turn outlived TurnTimeout, another replica may already hold it.
        await db.LockExtendAsync(TurnKey, turn.Token, nextSyncIn > TimeSpan.FromSeconds(1) ? nextSyncIn : TimeSpan.FromSeconds(1));
    }
}

/// <summary>The same schedule inside one process: for tests, and a host that runs a single replica.</summary>
public sealed class InProcessCartesiaSyncCoordinator : ICartesiaSyncCoordinator
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private string? _holder;
    private DateTimeOffset _heldUntil;
    private DateTimeOffset? _backfilledUntil;

    public InProcessCartesiaSyncCoordinator(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public Task<CartesiaSyncTurn?> TryBeginTurnAsync(TimeSpan backfillEvery, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_holder is not null && now < _heldUntil) return Task.FromResult<CartesiaSyncTurn?>(null);

            _holder = Guid.NewGuid().ToString("N");
            _heldUntil = now + RedisCartesiaSyncCoordinator.TurnTimeout;
            var backfill = _backfilledUntil is not { } until || now >= until;
            return Task.FromResult<CartesiaSyncTurn?>(new CartesiaSyncTurn(_holder, backfill));
        }
    }

    public Task EndTurnAsync(CartesiaSyncTurn turn, TimeSpan nextSyncIn, bool backfilled, TimeSpan backfillEvery, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (backfilled) _backfilledUntil = now + backfillEvery;
            if (_holder == turn.Token) _heldUntil = now + nextSyncIn;
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// <see cref="ICartesiaUsageSyncStatus"/> shared through Redis, so every replica's Insights snapshot
/// reports the sync that actually ran — including when a different replica ran it. Disabled is decided
/// by this replica's own configuration (all replicas share it) and never touches Redis. A Redis error
/// falls back to this process's view rather than failing the snapshot.
/// </summary>
public sealed class RedisCartesiaUsageSyncStatus : ICartesiaUsageSyncStatus
{
    public const string StatusKey = "billing:cartesia-usage-sync:status";

    /// <summary>Long enough to outlive any backoff; a status older than this is no status.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly IConnectionMultiplexer _redis;
    private readonly CartesiaUsageSyncStatus _local;
    private readonly ILogger<RedisCartesiaUsageSyncStatus> _logger;
    private readonly bool _configured;
    private readonly bool _filteredToApiKey;

    public RedisCartesiaUsageSyncStatus(
        IConnectionMultiplexer redis, bool configured, bool filteredToApiKey, ILogger<RedisCartesiaUsageSyncStatus> logger)
    {
        _redis = redis;
        _configured = configured;
        _filteredToApiKey = filteredToApiKey;
        _local = new CartesiaUsageSyncStatus(configured, filteredToApiKey);
        _logger = logger;
    }

    public async Task<CartesiaUsageSyncState> GetAsync(CancellationToken ct = default)
    {
        var local = await _local.GetAsync(ct);
        if (!_configured || local.Status == Domain.Constants.ProviderUsageConstants.SyncStatuses.Disabled) return local;

        try
        {
            var stored = await _redis.GetDatabase().StringGetAsync(StatusKey);
            if (stored.IsNullOrEmpty) return local;
            var shared = JsonSerializer.Deserialize<CartesiaUsageSyncState>(stored.ToString());
            return shared is null ? local : shared with { FilteredToApiKey = _filteredToApiKey };
        }
        catch (Exception ex) when (ex is RedisException or JsonException or TimeoutException)
        {
            _logger.LogDebug(ex, "Cartesia usage sync status could not be read from Redis; using this replica's view.");
            return local;
        }
    }

    public Task MarkDisabledAsync(string message, CancellationToken ct = default) => _local.MarkDisabledAsync(message, ct);

    public async Task<CartesiaUsageSyncState> MarkSucceededAsync(DateTime at, CancellationToken ct = default)
        => await WriteAsync(CartesiaUsageSyncStatus.Succeeded(await GetAsync(ct), at), ct);

    public async Task<CartesiaUsageSyncState> MarkFailedAsync(DateTime at, string message, CancellationToken ct = default)
        => await WriteAsync(CartesiaUsageSyncStatus.Failed(await GetAsync(ct), at, message), ct);

    private async Task<CartesiaUsageSyncState> WriteAsync(CartesiaUsageSyncState state, CancellationToken ct)
    {
        // The local copy first: if Redis is down, this replica still reports what it did.
        if (state.Status == Domain.Constants.ProviderUsageConstants.SyncStatuses.Ok)
            await _local.MarkSucceededAsync(state.LastSuccessAt ?? DateTime.UtcNow, ct);
        else
            await _local.MarkFailedAsync(state.LastAttemptAt ?? DateTime.UtcNow, state.Message ?? string.Empty, ct);

        try
        {
            await _redis.GetDatabase().StringSetAsync(StatusKey, JsonSerializer.Serialize(state), Retention);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogDebug(ex, "Cartesia usage sync status could not be written to Redis.");
        }

        return state;
    }
}
