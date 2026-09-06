using System;
using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// The four things a finished meeting leaves behind, listed as documents rather than as meetings.
///
/// The archive has always been meeting-shaped: one row per room, its outputs folded inside. That
/// answers "what meetings did we hold?" and cannot answer "where is that transcript?" — which is
/// the question people actually arrive with. This request is the document-shaped read of the same
/// data, so one page can hold every transcript, summary, minutes and recording in the workspace.
/// </summary>
public record GetMeetingDocumentsRequest(
    Guid? WorkspaceId = null,
    /// <summary>
    /// One of <see cref="MeetingDocumentTypes"/>, or null for all four. Comma-separated is
    /// accepted so the UI's filter chips can widen without a second round trip.
    /// </summary>
    string? Type = null,
    /// <summary>
    /// Matched against the MEETING's title, code and description — never against the document.
    /// `translation_room_artifacts` has no title column (the DTO's Title is generated), and a
    /// transcript's body is the wrong thing to full-text scan on a list read.
    /// </summary>
    string? Search = null,
    int Page = 1,
    int PageSize = 24);

/// <summary>
/// The document types this endpoint unions. `MINUTES` is the odd one: the other three are rows in
/// `translation_room_artifacts`, while minutes live in their own table with their own lifecycle.
/// </summary>
public static class MeetingDocumentTypes
{
    public const string TranscriptExport = "TRANSCRIPT_EXPORT";
    public const string SummaryExport = "SUMMARY_EXPORT";

    /// <summary>
    /// Stored as <c>OPTIONAL_RECORDING</c> — see <see cref="StoredArtifactTypeOf"/>. The wire name
    /// is the short one because that is what the web has always called it: its own
    /// <c>normalizeArtifactType</c> already folds `optional_recording` down to `recording`, and
    /// exporting the enum's spelling here would mean a third name for one thing.
    /// </summary>
    public const string Recording = "RECORDING";

    public const string Minutes = "MINUTES";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        TranscriptExport, SummaryExport, Recording, Minutes
    };

    /// <summary>The three that are rows in <c>translation_room_artifacts</c>.</summary>
    public static readonly IReadOnlySet<string> ArtifactBacked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        TranscriptExport, SummaryExport, Recording
    };

    /// <summary>
    /// The wire type as `translation_room_artifacts.artifact_type` spells it. Only RECORDING
    /// differs; matching on the wire name would silently return zero recordings forever.
    /// </summary>
    public static string StoredArtifactTypeOf(string wireType) =>
        string.Equals(wireType, Recording, StringComparison.OrdinalIgnoreCase)
            ? Domain.Enums.ArtifactType.OPTIONAL_RECORDING.ToString()
            : wireType.ToUpperInvariant();

    /// <summary>The inverse of <see cref="StoredArtifactTypeOf"/>, for rows coming back out.</summary>
    public static string WireTypeOf(string storedType) =>
        string.Equals(storedType, Domain.Enums.ArtifactType.OPTIONAL_RECORDING.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Recording
            : storedType.ToUpperInvariant();
}

/// <summary>
/// One document, with just enough of its meeting to render a card and just enough policy to know
/// whether clicking it will work.
///
/// Deliberately carries NO body. A transcript export runs to hundreds of kilobytes, and a grid of
/// twenty-four of them would move megabytes to render titles. The drawer fetches the one document
/// the reader opened, through the existing per-document endpoints that already enforce access —
/// so this list never becomes a second, looser way to read an artifact's content.
/// </summary>
public record MeetingDocumentDto(
    Guid Id,
    string Type,
    string Status,
    Guid TranslationRoomId,
    Guid WorkspaceId,
    string MeetingTitle,
    string TranslationRoomCode,
    string MeetingStatus,
    DateTime? MeetingEndedAt,
    string? SourceLanguage,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string? FileFormat,
    long? FileSizeBytes,
    bool ConsentRequired,
    /// <summary>
    /// Whether this caller may read the body, decided by the SAME predicate the download endpoint
    /// uses (<c>ArtifactAccessHelper.HasAccessToRoomArtifacts</c>). Sent so a card can show a lock
    /// up front instead of looking available and failing on click — room artifacts default to
    /// HOST_ONLY, so a participant seeing another person's summary listed is the common case.
    /// </summary>
    bool CanOpen,
    /// <summary>Set only on a MINUTES document — the document's own reference number.</summary>
    string? MinutesNo = null,
    /// <summary>Set only on a MINUTES document. Approved minutes are never edited in place.</summary>
    int? MinutesVersion = null,
    /// <summary>
    /// Whether the meeting this document belongs to already has minutes drawn up.
    ///
    /// Here so the grid can offer "Draw up the minutes" on a finished meeting that has none. That
    /// offer is the point of the field: minutes have been buildable end to end for weeks and the
    /// table holds zero rows, because the only door was four clicks deep inside one meeting.
    /// </summary>
    bool RoomHasMinutes = false,
    /// <summary>Whether this caller hosts the meeting — only the chair may draw up minutes.</summary>
    bool IsHost = false);

public record MeetingDocumentsResponse(
    List<MeetingDocumentDto> Documents,
    /// <summary>The server's count for the current filters — NOT <c>Documents.Count</c>.</summary>
    int Total,
    /// <summary>1-based.</summary>
    int Page,
    int PageSize);
