using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// Explicit room settings. Every field is nullable so "not sent" stays distinguishable from
/// "sent false" — the meeting type seeds anything left unset (see TranslationRoomTypePolicy),
/// and a non-null value here always wins over the type's default.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record RoomSettingsRequest(
    bool? RequiresApproval = null,
    string? ArtifactAccess = null,
    bool? MuteOnEntry = null,
    bool? AutoRecord = null,
    bool? BreakoutsEnabled = null
);

public record RoomSettingsResponse(
    bool RequiresApproval,
    string ArtifactAccess,
    bool MuteOnEntry,
    bool AutoRecord,
    bool BreakoutsEnabled
);

public record UpdateRoomSettingsRequest(
    string? Title,
    string? Description,
    int? MaxParticipants,
    DateTime? ScheduledAt,
    List<string>? InvitedEmails,
    RoomSettingsRequest? Settings,
    string? SourceLanguage,
    List<string>? TargetLanguages
);

public record GetTranslationRoomsRequest(
    string? Status = null,
    string? Search = null,
    DateTime? From = null,
    DateTime? To = null,
    int Page = 1,
    int PageSize = 20,
    Guid? WorkspaceId = null,
    /// <summary>
    /// WT-327: collapse the occurrences of a recurring booking into the ONE meeting the user
    /// booked, carrying its rule and its next date.
    ///
    /// Opt-in rather than always-on, because the two readers want opposite things and both are
    /// right. The meetings list asks "what meetings do I have?", and a daily standup is one
    /// answer, not fourteen. The home day panel asks "what is on Thursday?", and there the
    /// occurrence IS the meeting — collapsing would empty every day but one.
    /// </summary>
    bool GroupBySeries = false
);

/// <summary>
/// WT-327: the recurrence rule for a repeating booking.
///
/// Time is a WALL CLOCK plus an IANA zone, never a UTC instant. "8am daily" is a statement
/// about the clock on the wall in Ho Chi Minh City; an instant would drift the moment a zone's
/// rules change. The per-occurrence UTC <c>scheduledAt</c> is derived from this, once per
/// occurrence, at materialisation time.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record RecurrenceRequest(
    // One of RecurrenceTypes: DAILY, WEEKLY or MONTHLY.
    string Type,
    // Local time of day, "HH:mm" (24h). The hour the user picked in the repeat modal.
    [Required] string StartTimeLocal,
    // IANA zone id, e.g. "Asia/Ho_Chi_Minh". Not a UTC offset.
    [Required] string TimeZone,
    // Local date of the first occurrence, "yyyy-MM-dd". Defaults to today, or tomorrow when
    // today's time has already passed.
    string? StartDateLocal = null,
    // Local date of the last occurrence, INCLUSIVE, "yyyy-MM-dd". Omitted means
    // RecurrenceLimits.DefaultDurationDays from the start — never "forever".
    string? EndDateLocal = null,
    // WEEKLY only: ISO weekdays, Monday 1 … Sunday 7. Omitted means "the weekday the start date
    // lands on". Sending it on a DAILY or MONTHLY rule is a validation failure, not a no-op.
    List<int>? ByWeekdays = null,
    // MONTHLY only: day of the month 1–31. Omitted means "the start date's own day". A month with
    // fewer days than this simply has no occurrence — see RecurrenceScheduleCalculator.
    int? ByMonthDay = null
);

/// <summary>WT-327: what a room reports about the series it belongs to.</summary>
/// <param name="ByWeekdays">Populated for WEEKLY, null otherwise. ISO weekdays, ascending.</param>
/// <param name="ByMonthDay">Populated for MONTHLY, null otherwise.</param>
public record RecurrenceSummaryResponse(
    Guid SeriesId,
    string Type,
    string StartTimeLocal,
    string TimeZone,
    string StartDateLocal,
    string EndDateLocal,
    string Status,
    int Interval = 1,
    List<int>? ByWeekdays = null,
    int? ByMonthDay = null
);

public record CreateTranslationRoomRequest(
    Guid? WorkspaceId,
    [Required] string Title,
    string? Description,
    string TranslationRoomType, // one of TranslationRoomTypes
    // Optional: omit to let the meeting type decide the seat count.
    int? MaxParticipants,
    string? SourceLanguage,
    List<string>? TargetLanguages,
    RoomSettingsRequest? Settings,
    DateTime? ScheduledAt,
    List<string>? InvitedEmails,
    // WT-327: present means "this is a recurring booking, not a single meeting". Mutually
    // exclusive with ScheduledAt — the recurrence rule owns every occurrence's time, so a
    // second, contradictory time on the same request is rejected rather than silently ignored.
    RecurrenceRequest? Recurrence = null
);

/// <summary>WT-327: what creating a recurring booking returns.</summary>
public record CreateRecurringRoomResponse(
    RecurrenceSummaryResponse Series,
    // The first materialised occurrence — what the client navigates to and shares a code for.
    TranslationRoomDto FirstOccurrence,
    // How many occurrences exist right now. The rest arrive as the horizon rolls forward.
    int MaterializedOccurrenceCount,
    // How many the series will have in total once fully materialised.
    int TotalOccurrenceCount
);

public record JoinTranslationRoomRequest(
    [Required] string TranslationRoomCode,
    [Required] string DisplayName,
    string? SpeakLanguage,
    string? ListenLanguage
);

public record TranslationRoomDto(
    Guid Id,
    Guid WorkspaceId,
    Guid HostId,
    [MaxLength(255)] string Title,
    string? Description,
    [StringLength(12)] string TranslationRoomCode,
    RoomStatus Status,
    string TranslationRoomType,
    int MaxParticipants,
    string SourceLanguage,
    List<string> TargetLanguages,
    DateTime? ScheduledAt,
    List<string>? InvitedEmails,
    DateTime? StartedAt,
    DateTime? EndedAt,
    int? DurationSeconds,
    DateTime CreatedAt,
    RoomSettingsResponse Settings,
    // WT-280: how many people are actually in the room right now — CONNECTED participants only,
    // per TranslationRoomParticipantStatuses.SeatHolding. Room detail never carried this at all,
    // so the client's fallback for the list's occupancy was reading a field that did not exist.
    // Deliberately has no default: every construction site must supply a real count rather than
    // inherit a silent 0, which is precisely how the list bug went unnoticed.
    int ParticipantCount,
    List<RoomArtifactDto>? Artifacts = null,
    // WT-327: the recurring series this room is an occurrence of, or null for a one-off.
    // Optional with a default so every existing construction site — and every existing client —
    // is unaffected: a room that is not part of a series looks exactly as it always did.
    Guid? SeriesId = null,
    /// <summary>
    /// Distinct people who have ever been in this room. ParticipantCount answers "who is in
    /// there NOW" and is 0 for every finished meeting — which is why an ended room reported
    /// "0/100" however many people attended it. Trailing and defaulted so every existing
    /// positional construction site still compiles.
    /// </summary>
    int AttendedCount = 0
);

public record TranslationRoomListItemDto(
    Guid Id,
    Guid WorkspaceId,
    Guid HostId,
    [MaxLength(255)] string Title,
    string? Description,
    [StringLength(12)] string TranslationRoomCode,
    RoomStatus Status,
    string TranslationRoomType,
    int MaxParticipants,
    string SourceLanguage,
    List<string> TargetLanguages,
    DateTime? ScheduledAt,
    List<string>? InvitedEmails,
    DateTime? StartedAt,
    DateTime? EndedAt,
    int? DurationSeconds,
    DateTime CreatedAt,
    RoomSettingsResponse Settings,
    int ParticipantCount,
    bool IsHost,
    // WT-327: lets the meetings list and the day timeline mark an occurrence as repeating
    // without a second request. Null for every room that is not part of a series.
    Guid? SeriesId = null,
    // WT-327: the booking this row stands for, populated only when the list was asked to group
    // by series. Null on an ungrouped list even for an occurrence, so a caller can never mistake
    // "one occurrence of many" for "the whole booking".
    SeriesListSummaryDto? Series = null,
    /// <summary>
    /// Distinct people who have ever been in this room, whatever their status now.
    /// ParticipantCount is live occupancy and is 0 for every finished meeting, which is why an
    /// ended room showed "0/100" no matter how many attended.
    /// </summary>
    int AttendedCount = 0
);

/// <summary>
/// WT-327: what one row of a GROUPED meetings list says about the booking behind it — enough to
/// render "Weekly · Mon, Wed · 08:00 · next Thursday" and a count, without a request per row.
/// </summary>
/// <param name="OccurrenceCount">
/// Occurrences matching the SAME filters as the list itself, not the series' lifetime total. On
/// the active list that reads as "3 meetings still to come", which is the number the row is
/// standing in for; the lifetime total would claim meetings the row does not represent.
/// </param>
/// <param name="NextOccurrenceAt">
/// UTC start of the first occurrence at or after now, or null when every occurrence in range is
/// already past.
/// </param>
public record SeriesListSummaryDto(
    Guid SeriesId,
    string Type,
    int Interval,
    List<int>? ByWeekdays,
    int? ByMonthDay,
    string StartTimeLocal,
    string TimeZone,
    string Status,
    int OccurrenceCount,
    DateTime? NextOccurrenceAt
);

/// <summary>WT-327: the booking, its rule, and every occurrence of it this caller may see.</summary>
public record SeriesDetailResponse(
    RecurrenceSummaryResponse Series,
    Guid HostId,
    string Title,
    string? Description,
    string TranslationRoomType,
    string SourceLanguage,
    List<string> TargetLanguages,
    List<string> InvitedEmails,
    List<TranslationRoomListItemDto> Occurrences,
    // The occurrence a "join this booking" action should land on: the one running now, else the
    // next one due. Null once the whole series is in the past.
    Guid? CurrentOccurrenceId
);

/// <summary>
/// WT-327: an edit to the BOOKING — the template every future occurrence is stamped from.
///
/// Deliberately excludes the rule itself (cadence, time, dates). Moving a series to a different
/// hour has to decide what happens to the occurrences already materialised, already invited to,
/// and possibly already started, and that is a different feature with different confirmations.
/// Every field here is nullable: null means "leave it alone", so a client that knows about one
/// field cannot blank the others.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record UpdateSeriesRequest(
    string? Title = null,
    string? Description = null,
    int? MaxParticipants = null,
    string? SourceLanguage = null,
    List<string>? TargetLanguages = null,
    RoomSettingsRequest? Settings = null,
    List<string>? InvitedEmails = null
);

/// <summary>WT-327: what a series edit actually changed.</summary>
public record UpdateSeriesResult(
    Guid SeriesId,
    // Future occurrences the edit was applied to. Occurrences that already started keep the
    // details they ran with — rewriting a meeting that happened is not an edit, it is a lie.
    int UpdatedOccurrenceCount
);

public record TranslationRoomListResponse(
    List<TranslationRoomListItemDto> Rooms,
    int Total,
    int Page,
    int PageSize
);

public record JoinTranslationRoomResponse(
    TranslationRoomDto Room,
    TranslationRoomParticipantDto Participant
);

public record RoomArtifactDto(
    Guid Id,
    string ArtifactType,
    string? FileFormat,
    long? FileSizeBytes,
    bool ContainsRawAudio,
    bool ContainsRawVideo,
    bool ConsentRequired,
    DateTime? RetentionUntil,
    string Status,
    DateTime CreatedAt
);

public record TranslationRoomArtifactDto(
    Guid Id,
    Guid TranslationRoomId,
    string Type,
    string Title,
    string? FileUrl,
    string? FileFormat,
    long? FileSizeBytes,
    bool ContainsRawAudio,
    bool ContainsRawVideo,
    bool ConsentRequired,
    DateTime? RetentionUntil,
    string Status,
    DateTime CreatedAt,
    // WT-13: inline payload (e.g. AI meeting-summary JSON) for artifact types that don't
    // need external file storage.
    string? Content = null
);

public record CreateArtifactRequest(
    Guid RoomId,
    string ArtifactType,
    string? FileUrl,
    string FileFormat,
    long SizeBytes,
    bool ContainsRawAudio,
    bool ContainsRawVideo,
    bool ConsentRequired,
    DateTime? RetentionUntil = null,
    string? Content = null
);

public record TranslationRoomHistoryItemDto(
    TranslationRoomListItemDto Room,
    List<TranslationRoomParticipantDto> Participants,
    List<TranslationRoomArtifactDto> Artifacts
);

public record TranslationRoomHistoryResponse(
    List<TranslationRoomHistoryItemDto> Rooms,
    int Total,
    int Page,
    int PageSize
);

public record SubmitTranslationRoomFeedbackRequest(
    [Range(1, 5)] int OverallRating,
    [Range(1, 5)] int? TranslationQuality,
    [Range(1, 5)] int? AudioQuality,
    [Range(1, 5)] int? VoiceCloneQuality,
    [Range(1, 5)] int? AiSummaryQuality,
    string? Comments,
    Dictionary<string, object>? CommunicationInsights
);

public record TranslationRoomFeedbackDto(
    Guid Id,
    Guid TranslationRoomId,
    Guid UserId,
    int OverallRating,
    int? TranslationQuality,
    int? AudioQuality,
    int? VoiceCloneQuality,
    int? AiSummaryQuality,
    string? Comments,
    Dictionary<string, object>? CommunicationInsights,
    DateTime CreatedAt
);

public record TranslationRoomFeedbackStateDto(
    bool HasSubmitted,
    TranslationRoomFeedbackDto? Feedback
);
