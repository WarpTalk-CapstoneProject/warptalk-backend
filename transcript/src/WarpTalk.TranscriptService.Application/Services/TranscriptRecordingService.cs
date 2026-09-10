using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.Authorization;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Application.Mappers;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;

namespace WarpTalk.TranscriptService.Application.Services;

public class TranscriptRecordingService : ITranscriptRecordingService
{
    // Same Redis pub/sub channel TranslationRoomService already publishes RoomStarted/RoomEnded/
    // TranslationStopped on — TranslationRoomRedisSubscriberService (Gateway) is already
    // subscribed to it. Reusing the transport, not the event: "TranscriptPaused"/"TranscriptResumed"
    // are new command names, so a client that only knows the old commands ignores them, and
    // nothing here can be mistaken for "translation stopped" (which the AI workers key off of).
    private const string GatewayCommandsChannel = "warptalk:translation-room:commands";
    private const string TranscriptPausedCommand = "TranscriptPaused";
    private const string TranscriptResumedCommand = "TranscriptResumed";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranscriptPauseAccess _pauseAccess;
    private readonly ITranscriptReadAccess _readAccess;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ITranslationRoomEndTime? _roomEndTime;
    private readonly ILogger<TranscriptRecordingService> _logger;

    public TranscriptRecordingService(
        IUnitOfWork unitOfWork,
        ITranscriptPauseAccess pauseAccess,
        ITranscriptReadAccess readAccess,
        ILogger<TranscriptRecordingService> logger,
        IConnectionMultiplexer? redis = null,
        ITranslationRoomEndTime? roomEndTime = null)
    {
        _unitOfWork = unitOfWork;
        _pauseAccess = pauseAccess;
        _readAccess = readAccess;
        _logger = logger;
        _redis = redis;
        _roomEndTime = roomEndTime;
    }

    public async Task<Result> PauseAsync(Guid translationRoomId, Guid callerId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _pauseAccess.IsRoomHostAsync(translationRoomId, callerId, cancellationToken))
                return Result.Failure("Only the host can pause the transcript.", "FORBIDDEN");

            var active = await _unitOfWork.TranscriptPauseWindows.GetActiveWindowByRoomIdAsync(translationRoomId, cancellationToken);
            if (active != null)
                return Result.Failure("The transcript is already paused.", "INVALID_STATE");

            var now = DateTime.UtcNow;
            await _unitOfWork.TranscriptPauseWindows.AddAsync(new TranscriptPauseWindow
            {
                TranslationRoomId = translationRoomId,
                StartedAt = now,
                PausedBy = callerId,
                CreatedAt = now,
                UpdatedAt = now,
            }, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Order matters: the durable flag first, the pub/sub nudge second. A consumer woken by
            // the nudge immediately re-reads the flag, and doing it the other way round leaves a
            // window in which it reads "not paused" and never looks again.
            await SetPauseFlagAsync(translationRoomId, now, cancellationToken);
            await PublishCommandAsync(TranscriptPausedCommand, translationRoomId, cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing transcript for room {RoomId}", translationRoomId);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> ResumeAsync(Guid translationRoomId, Guid callerId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _pauseAccess.IsRoomHostAsync(translationRoomId, callerId, cancellationToken))
                return Result.Failure("Only the host can resume the transcript.", "FORBIDDEN");

            var active = await _unitOfWork.TranscriptPauseWindows.GetActiveWindowByRoomIdAsync(translationRoomId, cancellationToken);
            if (active == null)
                return Result.Failure("The transcript is not paused.", "INVALID_STATE");

            active.EndedAt = DateTime.UtcNow;
            active.ResumedBy = callerId;
            active.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.TranscriptPauseWindows.Update(active);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Clear before nudging, for the mirror of the reason Pause sets before nudging: a
            // consumer that re-reads on the event must not find the flag still standing.
            await ClearPauseFlagAsync(translationRoomId, cancellationToken);
            await PublishCommandAsync(TranscriptResumedCommand, translationRoomId, cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming transcript for room {RoomId}", translationRoomId);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<IReadOnlyList<TranscriptPauseWindowDto>>> GetPauseWindowsAsync(Guid translationRoomId, Guid callerId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Read access, not pause access: every participant sees the divider, not only the host.
            if (!await _readAccess.CanReadRoomTranscriptAsync(translationRoomId, callerId, cancellationToken))
                return Result.Failure<IReadOnlyList<TranscriptPauseWindowDto>>("You do not have access to this transcript.", "FORBIDDEN");

            var windows = await _unitOfWork.TranscriptPauseWindows.GetWindowsByRoomIdAsync(translationRoomId, cancellationToken);
            var dtos = windows.Select(w => w.ToDto()).ToList();

            return Result.Success<IReadOnlyList<TranscriptPauseWindowDto>>(
                await CloseWindowsLeftOpenByAFinishedRoomAsync(translationRoomId, dtos, cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing transcript pause windows for room {RoomId}", translationRoomId);
            return Result.Failure<IReadOnlyList<TranscriptPauseWindowDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    /// <summary>
    /// WT-605. A window still open on a room that has finished is shown ending when the ROOM did.
    /// </summary>
    /// <remarks>
    /// An open window means "paused right now" to every reader, and the panel renders it as
    /// <c>Transcript paused · 10:15 PM–now</c>. On a meeting that ended while paused that sentence
    /// is a lie that never expires: somebody opening the record next week is told the transcript is
    /// being held, at this moment, in a room nobody is in.
    ///
    /// CORRECTED WHERE IT IS READ, RATHER THAN REPAIRED WHERE IT IS STORED — and deliberately
    /// only here. A consumer on the RoomEnded announcement could stamp the row the moment the
    /// meeting finished, and an earlier draft of this ticket had one. It was removed: pub/sub has
    /// no backlog, so such a consumer can never be the only net anyway, and once this projection
    /// exists (it has to, for the reasons below) the consumer buys nothing a reader can see. What
    /// it costs is a hosted service running for the life of the process, and — worse — a second
    /// home for the rule "an open window on a finished room ends when the room did", which is
    /// exactly the kind of pair that drifts apart.
    ///
    /// Two things only a read-time projection catches at all:
    ///
    ///   * every row already in the table. Rooms ended while paused long before this ticket, and
    ///     their windows are open today. No migration can close them honestly — the transcript
    ///     schema does not know when a room ended, and a migration confined to it (the rule this
    ///     repo's transcript/database/migrations/README states) could only stamp <c>now()</c> or
    ///     <c>started_at</c>, both of which invent a fact rather than recover one. Reaching across
    ///     into translationRoom's tables from a transcript migration would recover the right value
    ///     by breaking that rule and coupling the two services' deploy order, which is a worse
    ///     trade for a display detail.
    ///   * a RoomEnded publish that was lost, or one that arrived while this process was down.
    ///
    /// The room is the authority on when it ended, and it is asked at the only moment the answer
    /// is needed. ENDED is terminal for a room (only PAUSED returns to IN_PROGRESS), so a window
    /// closed this way can never be reopened by a later session of the same room.
    ///
    /// A PROJECTION, NOT A WRITE. The stored row keeps its null, and every reader of this endpoint
    /// gets the corrected value. Repairing the row here instead would put a write in a GET that
    /// any participant can call, on a path with no lock, for a fact this service does not own.
    ///
    /// Costs one gRPC call, and only on a room that has an open window at all — the ordinary case
    /// is a list where every window is already closed, and that returns without touching the
    /// network. Absent lookup (or one that fails) leaves the list exactly as stored.
    /// </remarks>
    private async Task<IReadOnlyList<TranscriptPauseWindowDto>> CloseWindowsLeftOpenByAFinishedRoomAsync(
        Guid translationRoomId,
        List<TranscriptPauseWindowDto> windows,
        CancellationToken ct)
    {
        if (_roomEndTime is null || !windows.Any(w => w.EndedAt is null))
            return windows;

        var roomEndedAt = await _roomEndTime.GetEndedAtAsync(translationRoomId, ct);
        if (roomEndedAt is null)
            return windows;

        for (var i = 0; i < windows.Count; i++)
        {
            if (windows[i].EndedAt is not null)
                continue;

            // Never before the window opened. A room whose EndedAt somehow precedes the pause
            // would otherwise render as a negative stretch of time, which is a stranger thing to
            // show a reader than the open window this is fixing.
            windows[i] = windows[i] with
            {
                EndedAt = roomEndedAt < windows[i].StartedAt ? windows[i].StartedAt : roomEndedAt,
            };
        }

        return windows;
    }

    /// <summary>
    /// Project the pause onto <see cref="TranscriptPauseKey"/> — the durable half of this signal.
    /// </summary>
    /// <remarks>
    /// The pub/sub publish below it is not enough on its own and never was. Pub/sub has no
    /// backlog: a consumer that starts after the host pressed Pause is never told, and a consumer
    /// restarted mid-pause comes back believing the room is recording. Both are ordinary — the AI
    /// workers and the Gateway are separate deployments that restart on their own schedule — and
    /// both fail in the direction that writes down words somebody asked not to be written down.
    ///
    /// A key with a TTL fixes exactly that: whoever asks, whenever they ask, gets the answer.
    /// See <see cref="TranscriptPauseKey"/> for the shape and who else reads it.
    ///
    /// Best-effort for the same reason <see cref="PublishCommandAsync"/> is: the window is already
    /// committed to the database, which is the source of truth for the saved record and the
    /// divider. A Redis outage must degrade the live gate, not fail a Pause the host has already
    /// been told succeeded.
    /// </remarks>
    private async Task SetPauseFlagAsync(Guid roomId, DateTime pausedAtUtc, CancellationToken ct)
    {
        if (_redis is null)
            return;

        try
        {
            await _redis.GetDatabase().StringSetAsync(
                TranscriptPauseKey.For(roomId),
                TranscriptPauseKey.Payload(pausedAtUtc),
                TranscriptPauseKey.Ttl);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Logged at Warning, not Debug: while this is missing the live lanes believe the room
            // is recording, and that is the failure worth finding in a log afterwards.
            _logger.LogWarning(ex, "Failed to set the transcript-paused flag for RoomId: {RoomId}", roomId);
        }
    }

    /// <summary>
    /// Delete the flag. Deleting rather than writing "false" is the contract, not a shortcut —
    /// every reader tests existence, so a value nobody parses cannot drift out of agreement with
    /// this one.
    /// </summary>
    private async Task ClearPauseFlagAsync(Guid roomId, CancellationToken ct)
    {
        if (_redis is null)
            return;

        try
        {
            await _redis.GetDatabase().KeyDeleteAsync(TranscriptPauseKey.For(roomId));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Worse than the set failing: this one leaves a stale "paused" standing, so the live
            // transcript stays suppressed until the TTL expires or the room ends. The window is
            // already closed in the database, so the saved record and the divider are correct
            // regardless — but the meeting in progress is not.
            _logger.LogWarning(ex, "Failed to clear the transcript-paused flag for RoomId: {RoomId}", roomId);
        }
    }

    // Best-effort, same as PublishTranslationStoppedAsync on the translation-room side: a lost
    // publish only delays the live banner to a poll-driven refresh, and must never fail the
    // pause/resume call that already committed to the database.
    private async Task PublishCommandAsync(string command, Guid roomId, CancellationToken ct)
    {
        if (_redis is null)
            return;

        try
        {
            var payload = JsonSerializer.Serialize(new { Command = command, RoomId = roomId.ToString() });
            var subscriber = _redis.GetSubscriber();
            await subscriber.PublishAsync(RedisChannel.Literal(GatewayCommandsChannel), payload);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to publish {Command} for RoomId: {RoomId}", command, roomId);
        }
    }
}
