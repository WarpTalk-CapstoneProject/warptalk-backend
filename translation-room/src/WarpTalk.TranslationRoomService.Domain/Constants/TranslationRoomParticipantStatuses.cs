using System;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Domain.Constants;

/// <summary>
/// WT-262: the participant statuses that actually occupy a seat in a room.
///
/// Participant status is stored as text matching the <c>participant_status</c> Postgres enum
/// (INVITED, WAITING, CONNECTED, DISCONNECTED, LEFT, KICKED, REJECTED) and is compared as a string
/// throughout the service, so this exposes the seat rule as string constants rather than as
/// <see cref="TranslationRoomParticipantStatus"/> values.
/// </summary>
public static class TranslationRoomParticipantStatuses
{
    public const string Invited = nameof(TranslationRoomParticipantStatus.INVITED);
    public const string Waiting = nameof(TranslationRoomParticipantStatus.WAITING);
    public const string Connected = nameof(TranslationRoomParticipantStatus.CONNECTED);
    public const string Disconnected = nameof(TranslationRoomParticipantStatus.DISCONNECTED);
    public const string Left = nameof(TranslationRoomParticipantStatus.LEFT);
    public const string Kicked = nameof(TranslationRoomParticipantStatus.KICKED);
    public const string Rejected = nameof(TranslationRoomParticipantStatus.REJECTED);

    /// <summary>
    /// Statuses that consume one of the room's <see cref="Entities.TranslationRoom.MaxParticipants"/>
    /// seats. Only CONNECTED does — the same definition of "in the room" the roster already reports
    /// as <c>IsActive</c> over gRPC.
    ///
    /// RATIFIED PRODUCT DECISION (owner, 2026-08-05): a participant sitting in the LOBBY does NOT
    /// hold a seat. This is settled product policy, not an implementation guess left over from
    /// WT-262 — do not "fix" WAITING back in on the reasoning that a lobby participant is about to
    /// join. They are admitted one at a time and take their seat at admission.
    ///
    /// Everything else is deliberately excluded for the same reason: INVITED has not shown up;
    /// DISCONNECTED and LEFT released their seat when they dropped, so they must re-acquire one on
    /// return like anybody else; KICKED and REJECTED are terminal. Counting any of those would let a
    /// stale row lock a live user out of a room that has space, which is the failure mode this cap
    /// must not introduce.
    ///
    /// WT-263: this is now the SINGLE definition of "present in the room". IdleRoomMonitoringWorker
    /// shares it via <see cref="HoldsSeat"/> rather than carrying its own status predicate.
    /// </summary>
    public static readonly string[] SeatHolding = [Connected];

    /// <summary>True when <paramref name="status"/> currently occupies a seat.</summary>
    public static bool HoldsSeat(string? status) =>
        status is not null &&
        Array.Exists(SeatHolding, seat => string.Equals(seat, status, StringComparison.OrdinalIgnoreCase));
}
