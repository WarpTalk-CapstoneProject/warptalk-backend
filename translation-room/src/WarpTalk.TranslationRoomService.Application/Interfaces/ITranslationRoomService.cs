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
    Task<Result<IEnumerable<TranslationRoomInvitationDto>>> GetTranslationRoomInvitationsAsync(Guid translationRoomId, Guid userId, CancellationToken ct = default);
    Task<Result<JoinTranslationRoomResponse>> JoinTranslationRoomAsync(JoinTranslationRoomRequest request, Guid userId, string? userEmail = null, CancellationToken ct = default);
    Task<Result<TranslationRoomDto>> StartTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default);
    Task<Result> EndTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default);
    Task<Result<TranslationRoomDto>> CancelTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default);
    Task<Result> UpdateTranslationRoomSettingsAsync(Guid translationRoomId, Guid hostId, UpdateRoomSettingsRequest request, CancellationToken ct = default);

    // Lifecycle Controls
    Task<Result> OpenWaitingRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default);
    Task<Result> PauseTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default);
    Task<Result> ResumeTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default);
    Task<Result> ExpireTranslationRoomAsync(Guid translationRoomId, CancellationToken ct = default);

    Task<Result<TranslationRoomHistoryResponse>> GetTranslationRoomHistoryAsync(GetTranslationRoomsRequest request, Guid userId, string? userEmail = null, CancellationToken ct = default);
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
public record SeriesOccurrenceContext(Guid SeriesId, DateOnly LocalDate, bool SendInvitationEmails);
