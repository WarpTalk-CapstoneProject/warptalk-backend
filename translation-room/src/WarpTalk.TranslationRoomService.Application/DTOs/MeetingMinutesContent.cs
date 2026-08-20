using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// The structured body of a biên bản, stored in <c>meeting_minutes.content</c>.
///
/// Mutable properties with an explicit JSON name rather than a positional record: this round-trips
/// through the secretary's editor, so it must deserialise from whatever the client sends back, and
/// the names are a wire contract the web depends on.
/// </summary>
public class MeetingMinutesContent
{
    [JsonPropertyName("meetingTitle")]
    public string? MeetingTitle { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    /// <summary>When the meeting was called to order — the first participant's join.</summary>
    [JsonPropertyName("openedAt")]
    public DateTime? OpenedAt { get; set; }

    [JsonPropertyName("closedAt")]
    public DateTime? ClosedAt { get; set; }

    /// <summary>Kept beside the real opening time, because "started late" is itself a fact.</summary>
    [JsonPropertyName("scheduledAt")]
    public DateTime? ScheduledAt { get; set; }

    /// <summary>
    /// Chương trình họp. Null on a draft: there is no agenda field on a room — an agenda given at
    /// booking is folded into the description — so this is the secretary's to fill rather than
    /// something to guess at from prose.
    /// </summary>
    [JsonPropertyName("agenda")]
    public string? Agenda { get; set; }

    [JsonPropertyName("attendance")]
    public MinutesAttendance Attendance { get; set; } = new();

    [JsonPropertyName("sections")]
    public List<MinutesSection> Sections { get; set; } = new();

    [JsonPropertyName("votes")]
    public List<MinutesVote> Votes { get; set; } = new();

    /// <summary>Anything the secretary adds that the meeting's own record cannot supply.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// The language <see cref="Sections"/> is written in — the room's source language, i.e. the
    /// one the meeting was actually held in. Named so a bilingual document can label which column
    /// is the original rather than leaving a reader to guess from the script.
    /// </summary>
    [JsonPropertyName("primaryLanguage")]
    public string? PrimaryLanguage { get; set; }

    /// <summary>
    /// The same sections in each of the room's other languages, keyed by language code.
    ///
    /// Present only for a room with more than one target language — that is the only case in
    /// which the summary worker is asked for translations at all. PARTIAL by nature: the model is
    /// asked for {summary, decisions, actionItems}, so a template with other sections has no
    /// translation for them, and a missing language here means "not produced", never "empty".
    /// </summary>
    [JsonPropertyName("translations")]
    public Dictionary<string, List<MinutesSection>>? Translations { get; set; }
}

public class MinutesAttendance
{
    [JsonPropertyName("present")]
    public List<MinutesAttendee> Present { get; set; } = new();

    [JsonPropertyName("absent")]
    public List<MinutesAbsentee> Absent { get; set; } = new();

    [JsonPropertyName("invitedCount")]
    public int InvitedCount { get; set; }

    [JsonPropertyName("presentCount")]
    public int PresentCount { get; set; }

    /// <summary>The bar being applied, in words. A bare boolean would not say what it means.</summary>
    [JsonPropertyName("quorumRule")]
    public string? QuorumRule { get; set; }

    /// <summary>Null when nobody was formally invited — an ad-hoc room has no roll to be a majority of.</summary>
    [JsonPropertyName("quorumMet")]
    public bool? QuorumMet { get; set; }
}

public class MinutesAttendee
{
    [JsonPropertyName("participantId")]
    public Guid ParticipantId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("joinedAt")]
    public DateTime? JoinedAt { get; set; }

    [JsonPropertyName("leftAt")]
    public DateTime? LeftAt { get; set; }

    /// <summary>Not a member of the room's workspace when they joined — a guest, on the record as one.</summary>
    [JsonPropertyName("isExternal")]
    public bool IsExternal { get; set; }

    /// <summary>
    /// What language this person spoke. On a bilingual record this is what tells a reader which
    /// half of a quoted decision is the original and which is the translation.
    /// </summary>
    [JsonPropertyName("speakLanguage")]
    public string? SpeakLanguage { get; set; }
}

public class MinutesAbsentee
{
    [JsonPropertyName("participantId")]
    public Guid ParticipantId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Vắng có phép or không phép. The secretary's to state; the system cannot know it.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

public class MinutesSection
{
    /// <summary>
    /// The summary template's section key — "decisions", "actionItems", "blockers" and so on. The
    /// title is the web's to render, so this codebase does not hold a second copy of that list.
    /// </summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>"paragraph" or "items".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "items";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("items")]
    public List<MinutesItem>? Items { get; set; }
}

public class MinutesItem
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("owner")]
    public string? Owner { get; set; }

    /// <summary>
    /// Where in the meeting this came from. It is what lets a reader check a line against the
    /// transcript, which is the whole reason a summary item is allowed to appear in a signed
    /// document at all.
    /// </summary>
    [JsonPropertyName("atMs")]
    public long? AtMs { get; set; }
}

public class MinutesVote
{
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = string.Empty;

    [JsonPropertyName("forCount")]
    public int ForCount { get; set; }

    [JsonPropertyName("againstCount")]
    public int AgainstCount { get; set; }

    [JsonPropertyName("abstainCount")]
    public int AbstainCount { get; set; }

    [JsonPropertyName("atMs")]
    public long? AtMs { get; set; }
}
