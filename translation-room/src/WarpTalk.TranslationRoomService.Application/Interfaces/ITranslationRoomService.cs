using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface ITranslationRoomService
{
    /// <summary>
    /// Creates one room.
    ///
    /// WT-327: <paramref name="occurrence"/> is how a recurring series materialises one of its
    /// rooms. It is a METHOD parameter rather than a field on
    /// <see cref="CreateTranslationRoomRequest"/> on purpose — the request is bound straight
    /// from an HTTP body, and a client must not be able to attach its own room to somebody
    /// else's series. Only the series materialiser passes it.
    ///
    /// Every occurrence therefore goes through this exact method: same language validation,
    /// same workspace permission check, same room-code generation, same host participant seed,
    /// same settings resolution. A second creation path would be a second set of rules to keep
    /// in step, and it is the reason nothing downstream of a room needed to change at all.
    /// </summary>
    Task<Result<TranslationRoomDto>> CreateTranslationRoomAsync(
        CreateTranslationRoomRequest request,
        Guid hostId,
        CancellationToken ct = default,
        SeriesOccurrenceContext? occurrence = null);
    Task<Result<TranslationRoomListResponse>> GetTranslationRoomsAsync(GetTranslationRoomsRequest request, Guid userId, string? userEmail = null, CancellationToken ct = default);
    /// <summary>
    /// WT-334: the room detail read, for a HUMAN caller. <paramref name="userId"/> and
    /// <paramref name="userEmail"/> are required and positional, not optional — this method used to
    /// take neither, and <c>[Authorize]</c> on the controller was the entire check, so any
    /// authenticated user could read any room's title, description, code, schedule, settings and
    /// host across every tenant.
    ///
    /// The pair is deliberately NOT nullable-with-a-skip: a <c>userId</c> of null meaning "don't
    /// check" is the shape that lets a future call site opt out of authorization by accident. The
    /// one caller that genuinely has no user — the gRPC mesh — goes through
    /// <see cref="ITranslationRoomDirectoryService.GetRoomAsync"/> instead, which is a different
    /// interface with a different registration and no HTTP surface.
    ///
    /// A caller who may not read the room gets <c>ErrorCodes.NotFound</c>, identical to a room that
    /// does not exist. Distinguishing the two would confirm a room id to a cross-tenant prober,
    /// which is most of the value of the id.
    /// </summary>
    Task<Result<TranslationRoomDto>> GetTranslationRoomAsync(
        Guid translationRoomId,
        Guid userId,
        string? userEmail,
        CancellationToken ct = default);
    /// <summary>
    /// WT-327: every occurrence of one series that this caller may see, oldest first.
    ///
    /// Lives here, not on the series service, because "which rooms may this user see" is this
    /// service's question and it is already answered once, by the same query the meetings list
    /// uses. A series-side reimplementation would be a second copy of the visibility rules, and
    /// the copy that drifts is always the one guarding the read nobody tested.
    ///
    /// An empty list is a real answer — a series in a workspace this user has no part of — and is
    /// what lets the series read return not-found without a second authorization check.
    /// </summary>
    Task<Result<List<TranslationRoomListItemDto>>> GetSeriesOccurrencesAsync(
        Guid seriesId,
        Guid userId,
        string? userEmail,
        CancellationToken ct = default);

    Task<Result<IEnumerable<TranslationRoomInvitationDto>>> GetTranslationRoomInvitationsAsync(Guid translationRoomId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// The invitee's own answer to "you were invited": flips their PENDING invitation to ACCEPTED.
    ///
    /// Deliberately NOT the same act as joining. Joining seats you in the room and needs the room
    /// to be live; accepting is an RSVP to a meeting that is usually still in the future, and it is
    /// the only thing the invitation notification can offer at the moment it arrives.
    ///
    /// Keyed by the caller's EMAIL claim, because invitations are stored by email and an invitee
    /// may have no participant row and no workspace membership — the same identity
    /// <see cref="Domain.Authorization.RoomReadAccess"/> already resolves them by. A caller with no
    /// invitation of their own gets NotFound rather than Forbidden: whether a room exists is not
    /// something an uninvited caller may learn from this endpoint.
    ///
    /// Idempotent. Accepting an already-ACCEPTED invitation succeeds and returns the same row,
    /// because the button lives in a notification that can legitimately be clicked twice — from
    /// the popup and again from the bell.
    /// </summary>
    Task<Result<TranslationRoomInvitationDto>> AcceptTranslationRoomInvitationAsync(
        Guid translationRoomId,
        Guid userId,
        string? userEmail,
        CancellationToken ct = default);
    Task<Result<JoinTranslationRoomResponse>> JoinTranslationRoomAsync(JoinTranslationRoomRequest request, Guid userId, string? userEmail = null, CancellationToken ct = default);

    /// <summary>
    /// WT-468: which languages the pre-join screen may offer for a room identified by CODE.
    ///
    /// The rule is the owner's: a joiner is offered the languages the workspace that OWNS THE ROOM
    /// permits. The pre-join screen had no way to ask that — it has only a room code, and the room
    /// is not resolved until the join itself — so it fell back to the settings of whatever
    /// workspace the joiner happened to have selected. Someone in workspace A joining a room in
    /// workspace B was therefore offered A's languages: too few (B allows more) or too many
    /// (options B forbids, refused by the server only after they were picked).
    ///
    /// Returns an EMPTY list for a room whose workspace sets no policy, and for an external-bridge
    /// room, which belongs to no workspace. Empty means unrestricted everywhere this list travels.
    ///
    /// Deliberately NOT a preflight route. An earlier screen called a `preflight` endpoint that did
    /// not exist, 404'd on every request, and turned /join into a dead end blaming the room. This
    /// answers one question and returns nothing else about the room.
    /// </summary>
    Task<Result<JoinLanguagePolicyDto>> GetJoinLanguagePolicyByCodeAsync(
        string roomCode,
        CancellationToken ct = default);

    /// <summary>
    /// WT-480: who this meeting's record is shared with — its transcript, AI summary and recording.
    ///
    /// The visibility axis, and it is NOT the same as finalizing. Finalize
    /// (<c>TranscriptCorrectionService.FinalizeTranscriptAsync</c>) locks the wording so no more
    /// corrections land; this decides who may read it. A meeting can be shared and still editable,
    /// or locked and still private, and the two controls stay independent on purpose — folding
    /// them together would mean a typo could never be fixed once the record was shared.
    ///
    /// Deliberately NOT routed through <see cref="UpdateTranslationRoomSettingsAsync"/>, even
    /// though that method also writes <c>ArtifactAccess</c>: it refuses any room past WAITING, and
    /// sharing a record can only happen once the meeting is over and the artifacts exist. The
    /// feature would have been refused in exactly the state it is for.
    ///
    /// Host only, matching Finalize. <paramref name="level"/> must be one of
    /// <c>ArtifactAccessLevels.All</c>; anything else is rejected rather than stored, because an
    /// unrecognised level reads as HOST_ONLY at the guard and would silently deny everyone.
    /// </summary>
    Task<Result> SetArtifactAccessAsync(
        Guid translationRoomId,
        Guid hostId,
        string level,
        CancellationToken ct = default);

    /// <summary>
    /// WT-433: join by room ID — the shape a shared LINK produces — gated on membership of the
    /// room's workspace, then identical to the by-code join (a requires-approval room lands the
    /// caller in the waiting room). Non-members get NotFound, indistinguishable from a missing room.
    /// </summary>
    Task<Result<JoinTranslationRoomResponse>> JoinTranslationRoomByIdAsync(Guid translationRoomId, JoinTranslationRoomRequest request, Guid userId, string? userEmail = null, CancellationToken ct = default);
    /// <summary>
    /// WT-341: takes a room live. The caller is no longer required to be the host.
    ///
    /// A meeting whose host is busy used to be unstartable by anyone, which made "the host must
    /// open it" a way to lose the meeting rather than a way to control it. Whether someone other
    /// than the host may open the room is decided by the room's own <c>RequiresApproval</c>
    /// setting, which already existed and already means "entry is the host's decision":
    ///
    ///  - <c>RequiresApproval = false</c> — anyone who may be in the room may open it. Nobody is
    ///    waiting on a host decision, so nothing is bypassed by starting without them.
    ///  - <c>RequiresApproval = true</c> — host only, unchanged. Every non-host lands in the lobby
    ///    and the host is the one person who can admit them; letting a guest start the room would
    ///    open a meeting whose door nobody can answer.
    ///
    /// <paramref name="callerEmail"/> is required and positional for the same reason it is on
    /// <see cref="GetTranslationRoomAsync"/>: entitlement is host OR participant OR
    /// invited-by-email, and a nullable "skip the check" parameter is the shape that lets a call
    /// site opt out of authorization by accident. Pass null only when the caller genuinely has no
    /// email claim — that narrows the check to host-or-participant, it never widens it.
    /// </summary>
    Task<Result<TranslationRoomDto>> StartTranslationRoomAsync(
        Guid translationRoomId,
        Guid callerId,
        string? callerEmail,
        CancellationToken ct = default);
    Task<Result> EndTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default);
    Task<Result<TranslationRoomDto>> CancelTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default);
    Task<Result> UpdateTranslationRoomSettingsAsync(Guid translationRoomId, Guid hostId, UpdateRoomSettingsRequest request, CancellationToken ct = default);

    // Lifecycle Controls
    Task<Result> OpenWaitingRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default);
    Task<Result> PauseTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default);
    Task<Result> ResumeTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default);

    /// <summary>
    /// Stops TRANSLATION and leaves the meeting running.
    ///
    /// This is the other half of the split <see cref="ResumeTranslationRoomAsync"/> begins: that
    /// method is Start Translation, and this is Stop. The room stays IN_PROGRESS throughout, so
    /// transcription continues — the meeting is still open, people are still talking, and the
    /// transcript is what a meeting produces whether or not anyone is translating it.
    ///
    /// Deliberately NOT <see cref="PauseTranslationRoomAsync"/>, which is what Stop used to call.
    /// Pause moves the room to PAUSED, and the AI workers read PAUSED as "ignore this room's
    /// microphone" — correct for a pause, but it meant stopping translation also stopped the
    /// transcript, so "transcript only" was unreachable from a room that had ever translated.
    ///
    /// Idempotent: stopping a room that is not translating succeeds and does nothing.
    /// </summary>
    Task<Result> StopTranslationAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default);
    Task<Result> ExpireTranslationRoomAsync(Guid translationRoomId, CancellationToken ct = default);

    Task<Result<TranslationRoomHistoryResponse>> GetTranslationRoomHistoryAsync(GetTranslationRoomsRequest request, Guid userId, string? userEmail = null, CancellationToken ct = default);

    /// <summary>
    /// WT-333 — the caller's own meetings in one workspace, past and upcoming, newest first. Same
    /// response shape as the history read; the implementation documents the three ways it differs.
    /// </summary>
    Task<Result<TranslationRoomHistoryResponse>> GetMyMeetingsAsync(GetTranslationRoomsRequest request, Guid userId, string? userEmail = null, CancellationToken ct = default);

    Task<Result<List<TranslationRoomArtifactDto>>> GetTranslationRoomArtifactsAsync(Guid translationRoomId, Guid userId, string? userEmail = null, CancellationToken ct = default);
    Task<Result<TranslationRoomFeedbackStateDto>> GetFeedbackStateAsync(Guid translationRoomId, Guid userId, string? userEmail = null, CancellationToken ct = default);
    Task<Result<TranslationRoomFeedbackDto>> SubmitFeedbackAsync(Guid translationRoomId, Guid userId, SubmitTranslationRoomFeedbackRequest request, string? userEmail = null, CancellationToken ct = default);

    /// <summary>WT-14: builds a downloadable .ics calendar invite for a scheduled room.</summary>
    Task<Result<string>> GenerateCalendarIcsAsync(Guid translationRoomId, CancellationToken ct = default);
}

/// <summary>
/// WT-327: server-side-only provenance for a room that is one occurrence of a recurring series.
/// Never bound from a request body.
/// </summary>
/// <param name="SeriesId">The series this room belongs to.</param>
/// <param name="LocalDate">The series-local calendar date this occurrence is for. Unique per series.</param>
/// <param name="SendInvitationEmails">
/// True only for the occurrence created at series-creation time. Every occurrence still gets
/// invitation ROWS — otherwise an invitee would not see days 2..N in their meeting list at all —
/// but only one email goes out, because thirty identical "you're invited" emails for one daily
/// booking is spam, not a feature.
/// </param>
/// <param name="SharedRoomCode">
/// The code every occurrence of this series answers to, or null for the first one — which mints
/// it. One booking is one thing to share; a code per day meant Monday's invite opened Monday's
/// room for the rest of the month.
/// </param>
public record SeriesOccurrenceContext(
    Guid SeriesId,
    DateOnly LocalDate,
    bool SendInvitationEmails,
    string? SharedRoomCode = null);
