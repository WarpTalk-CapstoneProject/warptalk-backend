using System.Text.Json;
using Microsoft.Extensions.Logging;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Services;

/// <inheritdoc cref="IEgressReconciliation" />
public sealed class EgressReconciliationService : IEgressReconciliation
{
    /// <summary>
    /// How long an egress LiveKit has never heard of may keep a room marked "recording" before we
    /// give up on it.
    ///
    /// LiveKit forgets old egresses, so "unknown id" is ambiguous: it means either "you just
    /// started this and my read replica has not caught up" or "this finished days ago and aged
    /// out". Waiting an hour makes the first reading impossible, so the second is the only one
    /// left. Without the wait the sweep would race a recording that started seconds ago and clear
    /// a room that really is recording.
    /// </summary>
    private static readonly TimeSpan UnknownEgressGrace = TimeSpan.FromHours(1);

    /// <summary>
    /// LiveKit's terminal EgressStatus values. Anything else — STARTING, ACTIVE, ENDING — means
    /// the recording is still happening and the room is right to say so.
    ///
    /// Matched case-insensitively on the string form, because Twirp JSON serialises a proto enum
    /// as its name while some clients send the integer; the numeric fallback below covers that.
    /// </summary>
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "EGRESS_COMPLETE",
        "EGRESS_FAILED",
        "EGRESS_ABORTED",
        "EGRESS_LIMIT_REACHED"
    };

    private readonly IUnitOfWork _unitOfWork;
    private readonly ILiveKitEgressService _egressService;
    private readonly IEgressCompletion _egressCompletion;
    private readonly ILogger<EgressReconciliationService> _logger;

    public EgressReconciliationService(
        IUnitOfWork unitOfWork,
        ILiveKitEgressService egressService,
        IEgressCompletion egressCompletion,
        ILogger<EgressReconciliationService> logger)
    {
        _unitOfWork = unitOfWork;
        _egressService = egressService;
        _egressCompletion = egressCompletion;
        _logger = logger;
    }

    public async Task<Result<int>> ReconcileAsync(DateTime utcNow, CancellationToken ct = default)
    {
        var pending = await _unitOfWork.MeetingRoomRepository.FindAsync(
            room => room.ActiveEgressId != null,
            "",
            ct);

        var rooms = pending.ToList();
        if (rooms.Count == 0) return Result.Success(0);

        var finished = 0;
        foreach (var room in rooms)
        {
            ct.ThrowIfCancellationRequested();

            var egressId = room.ActiveEgressId;
            if (string.IsNullOrWhiteSpace(egressId)) continue;

            try
            {
                var lookup = await _egressService.GetEgressAsync(egressId, ct);
                if (!lookup.IsSuccess)
                {
                    // LiveKit unreachable. Leave the room exactly as it is and try next tick —
                    // clearing on a transport failure would tell a host their live recording had
                    // stopped when it had not.
                    continue;
                }

                if (lookup.Value is not JsonElement info)
                {
                    if (utcNow - room.UpdatedAt < UnknownEgressGrace) continue;

                    _logger.LogWarning(
                        "LiveKit does not know egress {EgressId}; clearing it from room {RoomId} after {Grace}. "
                        + "No recording artifact can be produced for it.",
                        egressId,
                        room.Id,
                        UnknownEgressGrace);
                    room.ActiveEgressId = null;
                    finished++;
                    continue;
                }

                if (!IsTerminal(info)) continue;

                var outcome = await _egressCompletion.ApplyAsync(info, ct);
                finished++;

                // Logged at Warning when the recording produced nothing, because that is the case
                // a host experiences as "I pressed record and got no video" — the exact report
                // this whole sweep came from. It must not read as routine success in the log.
                if (outcome == EgressCompletionOutcome.Published)
                {
                    _logger.LogInformation(
                        "Reconciled finished egress {EgressId} for room {RoomId}; recording event published. "
                        + "The egress_ended webhook did not deliver it.",
                        egressId,
                        room.Id);
                }
                else
                {
                    _logger.LogWarning(
                        "Egress {EgressId} for room {RoomId} finished with no recording file ({Outcome}).",
                        egressId,
                        room.Id,
                        outcome);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One bad room must not abandon the rest of the batch — it is the room whose
                // recording is already in trouble, and the others are waiting behind it.
                _logger.LogError(ex, "Failed to reconcile egress {EgressId} for room {RoomId}", egressId, room.Id);
            }
        }

        if (finished > 0) await _unitOfWork.SaveChangesAsync();
        return Result.Success(finished);
    }

    private static bool IsTerminal(JsonElement info)
    {
        if (!info.TryGetProperty("status", out var status)) return false;

        if (status.ValueKind == JsonValueKind.String)
            return TerminalStatuses.Contains(status.GetString() ?? string.Empty);

        // Proto enum ordinals: 0 STARTING, 1 ACTIVE, 2 ENDING, 3 COMPLETE, 4 FAILED, 5 ABORTED,
        // 6 LIMIT_REACHED. Only used when a client sends the numeric form.
        return status.ValueKind == JsonValueKind.Number
            && status.TryGetInt32(out var ordinal)
            && ordinal >= 3;
    }
}
