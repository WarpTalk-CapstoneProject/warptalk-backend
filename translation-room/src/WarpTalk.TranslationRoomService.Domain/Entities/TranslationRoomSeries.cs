using System;
using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

/// <summary>
/// WT-327: a recurring booking. NOT a meeting.
///
/// THE INVARIANT THIS ROW EXISTS TO PROTECT
///   Everything downstream of a room — billing, transcripts, artifacts, seat/occupancy
///   counting, the reminder sweep, the AI pipeline — assumes one `translation_rooms` row is
///   exactly one meeting. Making a single room row mean "N meetings" would have to be answered
///   in every one of those systems ("which occurrence is this transcript for?"), and that tail
///   is unbounded.
///
///   So a series stores no meeting state at all. It stores the RULE plus the TEMPLATE, and a
///   background worker materialises each occurrence as an ordinary TranslationRoom row that
///   points back here through <see cref="TranslationRoom.SeriesId"/>. Every existing feature
///   keeps working without knowing series exist; the only thing that had to learn anything new
///   is the code that creates and cancels the series.
///
/// TIME IS STORED LOCAL, COMPARED IN UTC
///   <see cref="StartTimeLocal"/> + <see cref="TimeZone"/> is the user's actual intent ("8am in
///   Ho Chi Minh City"), and it survives a DST rule change because it is re-resolved on every
///   materialisation. The derived instant lands on `translation_rooms.scheduled_at`, which is
///   UTC exactly as it has always been — so no reader downstream changes.
/// </summary>
public class TranslationRoomSeries
{
    public Guid Id { get; set; }

    /// <summary>External AuthService workspace id. No physical FK.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>External AuthService user id. No physical FK. The only identity that may cancel the series.</summary>
    public Guid HostId { get; set; }

    // ── The rule ──────────────────────────────────────────────────────────────

    /// <summary>One of <see cref="Constants.RecurrenceTypes"/>. Only DAILY is materialised today.</summary>
    public string RecurrenceType { get; set; } = Constants.RecurrenceTypes.Daily;

    /// <summary>Every N periods. Always 1 for the DAILY series the UI can currently create.</summary>
    public int RecurrenceInterval { get; set; } = 1;

    /// <summary>
    /// WEEKLY only: JSON array of ISO-8601 weekday numbers (1 = Monday … 7 = Sunday), e.g.
    /// <c>[1,3,5]</c>. Null for DAILY. Present now so that WEEKLY needs no migration later.
    /// </summary>
    public string? RecurrenceByWeekdays { get; set; }

    /// <summary>
    /// MONTHLY only: day-of-month 1–31. Null for DAILY. Present now so that MONTHLY needs no
    /// migration later.
    /// </summary>
    public int? RecurrenceByMonthDay { get; set; }

    /// <summary>Wall-clock time of day the host picked, in <see cref="TimeZone"/>.</summary>
    public TimeOnly StartTimeLocal { get; set; }

    /// <summary>IANA time zone id, e.g. <c>Asia/Ho_Chi_Minh</c>. Never a UTC offset — an offset cannot survive a DST rule change.</summary>
    public string TimeZone { get; set; } = null!;

    /// <summary>Local calendar date of the first candidate occurrence, inclusive.</summary>
    public DateOnly StartsOnLocalDate { get; set; }

    /// <summary>
    /// Local calendar date of the last candidate occurrence, INCLUSIVE. Not nullable, on
    /// purpose: an indefinite series generates rooms forever for workspaces nobody will ever
    /// open again. See <see cref="Constants.RecurrenceLimits.DefaultDurationDays"/>.
    /// </summary>
    public DateOnly EndsOnLocalDate { get; set; }

    // ── Bookkeeping ───────────────────────────────────────────────────────────

    /// <summary>One of <see cref="Constants.RecurrenceSeriesStatuses"/>.</summary>
    public string Status { get; set; } = Constants.RecurrenceSeriesStatuses.Active;

    /// <summary>
    /// Last local date this series has been materialised through, inclusive. The rolling
    /// horizon's watermark: the worker only ever considers dates strictly after it, which is
    /// what makes cancelling a single occurrence permanent — the sweep never revisits its date.
    /// Null means nothing has been materialised yet.
    /// </summary>
    public DateOnly? MaterializedThroughLocalDate { get; set; }

    // ── The template every occurrence is stamped from ─────────────────────────

    public string Title { get; set; } = null!;

    public string? Description { get; set; }

    public string TranslationRoomType { get; set; } = null!;

    public int MaxParticipants { get; set; }

    public string SourceLanguage { get; set; } = null!;

    /// <summary>JSON array, same shape as <see cref="TranslationRoom.TargetLanguages"/>.</summary>
    public string TargetLanguages { get; set; } = null!;

    /// <summary>JSON object, same shape as <see cref="TranslationRoom.Settings"/>.</summary>
    public string Settings { get; set; } = null!;

    /// <summary>JSON array of invited email addresses. Copied onto every occurrence as invitation rows.</summary>
    public string InvitedEmails { get; set; } = "[]";

    // ── Audit ─────────────────────────────────────────────────────────────────

    public DateTime CreatedAt { get; set; }

    /// <summary>External AuthService user id. No physical FK.</summary>
    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>External AuthService user id. No physical FK.</summary>
    public Guid? UpdatedBy { get; set; }

    public virtual ICollection<TranslationRoom> Occurrences { get; set; } = new List<TranslationRoom>();
}
