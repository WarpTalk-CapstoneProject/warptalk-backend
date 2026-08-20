using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// Assembles the draft of a biên bản họp out of what the meeting already knows.
///
/// WHY THIS IS ASSEMBLY AND NOT GENERATION
///     Standard minutes need a chair, a secretary, an attendance list, who was invited and did
///     not come, when the meeting opened and closed, and the substance of what was decided. Only
///     the last of those is a language problem, and a model already wrote it. Everything else is
///     a fact this service holds and has never put on a page — asking a model to restate facts
///     it can only read back out of a transcript is how a record acquires errors it did not have.
///
///     So: attendance comes from translation_room_participants, times come from the room and its
///     participants, and the narrative comes from the summary artifact verbatim. Nothing here
///     calls a model.
///
/// WHY THE OPENING TIME IS THE FIRST JOIN
///     `TranslationRoomSession.StartedAt` looks like the answer and is not: a session opens when
///     somebody presses Start Translation, which WT-248 made a deliberate manual act — a meeting
///     that never turned translation on has no session at all. `TranslationRoom.StartedAt` is
///     worse: nothing has ever written it. The first participant to join is when the meeting was
///     called to order, and it is a fact the room genuinely records.
///
/// WHY SECTIONS CARRY A KEY AND NOT A TITLE
///     Section titles live in warptalk-ai's summary_templates.py and are rendered by the web,
///     which already maps key to title. Copying that mapping here would make a third list that
///     has to move in step with the other two, and the one that drifts is always the copy nobody
///     is looking at.
/// </summary>
public static class MeetingMinutesDrafter
{
    /// <summary>The engine string recorded on the draft. Never the answerable party.</summary>
    public const string DraftEngine = "warptalk-ai/meeting-summary";

    /// <summary>
    /// Statuses that mean a participant was actually in the meeting at some point. DISCONNECTED
    /// counts: someone whose network dropped attended, and leaving them out of the attendance
    /// list would understate the room — including for quorum.
    /// </summary>
    private static readonly HashSet<string> AttendedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONNECTED", "DISCONNECTED", "LEFT", "KICKED"
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Property names on the summary JSON that are not narrative sections.</summary>
    private static readonly HashSet<string> NonSectionKeys = new(StringComparer.Ordinal)
    {
        "summary", "citations", "translations", "templateKey", "insufficientData", "sections"
    };

    public static string BuildContent(
        TranslationRoom room,
        IReadOnlyCollection<TranslationRoomParticipant> participants,
        string? summaryJson)
    {
        var attended = participants.Where(p => Attended(p)).ToList();

        var content = new MeetingMinutesContent
        {
            MeetingTitle = room.Title,
            // Every WarpTalk meeting is online; standard form still wants the line filled, and
            // naming the platform is the truthful answer to "địa điểm".
            Location = "Trực tuyến qua WarpTalk",
            OpenedAt = FirstJoin(attended),
            ClosedAt = room.EndedAt,
            ScheduledAt = room.ScheduledAt,
            Agenda = null,
            Attendance = BuildAttendance(participants, attended),
            Sections = BuildSections(summaryJson),
            // The language the meeting was actually held in, so a bilingual document can say
            // which half is the original instead of leaving a reader to infer it.
            PrimaryLanguage = string.IsNullOrWhiteSpace(room.SourceLanguage) ? null : room.SourceLanguage,
            Translations = BuildTranslations(summaryJson),
            // Votes are never inferred from the transcript. A count of who agreed has to come
            // from people pressing a button, because silence is not assent and "ừ" may be
            // answering a different question — a fabricated tally is worse than no tally.
            Votes = new List<MinutesVote>()
        };

        return JsonSerializer.Serialize(content, SerializerOptions);
    }

    /// <summary>
    /// How many items differ between two versions of the content, for
    /// <c>MeetingMinutes.EditCountVsDraft</c>. Deliberately coarse: the reader is being told
    /// "a person changed this much", not given a diff.
    /// </summary>
    public static int CountEdits(string? draftJson, string? editedJson)
    {
        if (string.IsNullOrWhiteSpace(draftJson) || string.IsNullOrWhiteSpace(editedJson)) return 0;

        try
        {
            var draft = JsonSerializer.Deserialize<MeetingMinutesContent>(draftJson, SerializerOptions);
            var edited = JsonSerializer.Deserialize<MeetingMinutesContent>(editedJson, SerializerOptions);
            if (draft == null || edited == null) return 0;

            var edits = 0;
            if (!string.Equals(draft.Agenda ?? "", edited.Agenda ?? "", StringComparison.Ordinal)) edits++;
            if (!string.Equals(draft.Notes ?? "", edited.Notes ?? "", StringComparison.Ordinal)) edits++;

            var draftItems = Flatten(draft.Sections);
            var editedItems = Flatten(edited.Sections);

            // Symmetric difference: a line rewritten counts once as removed and once as added,
            // which overstates a single edit by one. Left as is — the number is evidence that
            // somebody worked on the draft, not an audit trail, and understating it would be the
            // worse error of the two.
            edits += draftItems.Except(editedItems, StringComparer.Ordinal).Count();
            edits += editedItems.Except(draftItems, StringComparer.Ordinal).Count();
            return edits;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static List<string> Flatten(List<MinutesSection>? sections)
    {
        if (sections == null) return new List<string>();
        return sections
            .SelectMany(section => new[] { section.Text ?? "" }
                .Concat((section.Items ?? new List<MinutesItem>())
                    .Select(item => $"{section.Key}|{item.Owner}|{item.Text}")))
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .ToList();
    }

    /// <summary>When the meeting was called to order, or null when nobody ever joined.</summary>
    private static DateTime? FirstJoin(IReadOnlyCollection<TranslationRoomParticipant> attended)
    {
        var joins = attended.Where(p => p.JoinedAt.HasValue).Select(p => p.JoinedAt!.Value).ToList();
        return joins.Count == 0 ? null : joins.Min();
    }

    private static bool Attended(TranslationRoomParticipant participant)
    {
        // JoinedAt is the harder evidence and it is what the opening time is drawn from, but a
        // status of LEFT with no JoinedAt still describes somebody who was here.
        return participant.JoinedAt.HasValue
            || (participant.Status != null && AttendedStatuses.Contains(participant.Status));
    }

    private static MinutesAttendance BuildAttendance(
        IReadOnlyCollection<TranslationRoomParticipant> all,
        IReadOnlyCollection<TranslationRoomParticipant> attended)
    {
        var attendedIds = attended.Select(p => p.Id).ToHashSet();

        var absent = all
            .Where(p => !attendedIds.Contains(p.Id))
            .Select(p => new MinutesAbsentee
            {
                ParticipantId = p.Id,
                Name = p.DisplayName,
                // Recorded, not judged. Standard form distinguishes vắng có phép from vắng không
                // phép and this service cannot know which; the secretary fills that in.
                Reason = null
            })
            .ToList();

        var invited = all.Count;
        var present = attended.Count;

        return new MinutesAttendance
        {
            Present = attended
                .OrderBy(p => p.JoinedAt ?? DateTime.MaxValue)
                .Select(p => new MinutesAttendee
                {
                    ParticipantId = p.Id,
                    Name = p.DisplayName,
                    Role = p.Role,
                    JoinedAt = p.JoinedAt,
                    LeftAt = p.LeftAt,
                    IsExternal = p.IsExternal,
                    SpeakLanguage = p.SpeakLanguage
                })
                .ToList(),
            Absent = absent,
            InvitedCount = invited,
            PresentCount = present,
            // Stated rather than assumed, because a bare boolean tells the reader nothing about
            // what bar was applied. Null when nobody was formally invited: an ad-hoc room has no
            // roll to be a majority of, and answering "false" there would be a claim, not a fact.
            QuorumRule = invited > 0 ? "Quá bán số người được mời" : null,
            QuorumMet = invited > 0 ? present * 2 > invited : null
        };
    }

    private static List<MinutesSection> BuildSections(string? summaryJson)
    {
        var sections = new List<MinutesSection>();
        if (string.IsNullOrWhiteSpace(summaryJson)) return sections;

        try
        {
            using var doc = JsonDocument.Parse(summaryJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return sections;

            // An insufficient-data summary carries a status message where the overview goes.
            // Copying it into a minutes document would put "the assistant could not generate a
            // summary" under a heading, signed by a person.
            if (root.TryGetProperty("insufficientData", out var insufficient) &&
                insufficient.ValueKind == JsonValueKind.True)
            {
                return sections;
            }

            if (root.TryGetProperty("summary", out var overview) &&
                overview.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(overview.GetString()))
            {
                sections.Add(new MinutesSection
                {
                    Key = "summary",
                    Kind = "paragraph",
                    Text = overview.GetString()
                });
            }

            foreach (var property in root.EnumerateObject())
            {
                if (NonSectionKeys.Contains(property.Name)) continue;
                if (property.Value.ValueKind != JsonValueKind.Array) continue;

                var items = ReadItems(property.Value);
                if (items.Count == 0) continue;

                sections.Add(new MinutesSection
                {
                    Key = property.Name,
                    Kind = "items",
                    Items = items
                });
            }
        }
        catch (JsonException)
        {
            // A summary that is not JSON is an older artifact holding plain text. It is still the
            // meeting's narrative, so it goes in as the overview rather than being dropped.
            sections.Add(new MinutesSection
            {
                Key = "summary",
                Kind = "paragraph",
                Text = summaryJson.Trim()
            });
        }

        return sections;
    }

    /// <summary>
    /// The summary's translated copies, normalised into the same section shape as the original.
    ///
    /// WHY THIS IS NOT A MIRROR OF <see cref="BuildSections"/>
    ///     The summary worker asks the model for {summary, decisions, actionItems} per language —
    ///     three keys, not the template's full section set. So a technical meeting's "problems"
    ///     and "options" have no translation and never will under the current contract. Returning
    ///     only what exists keeps that visible; padding the gap with empty sections would make a
    ///     document claim a translation it does not have.
    ///
    ///     The map is also model-produced and never defaulted upstream, so it can be absent from a
    ///     multilingual room's summary entirely. Absent means "not produced", never "none".
    /// </summary>
    private static Dictionary<string, List<MinutesSection>>? BuildTranslations(string? summaryJson)
    {
        if (string.IsNullOrWhiteSpace(summaryJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(summaryJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("insufficientData", out var insufficient) &&
                insufficient.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            if (!root.TryGetProperty("translations", out var translations) ||
                translations.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new Dictionary<string, List<MinutesSection>>(StringComparer.OrdinalIgnoreCase);

            foreach (var language in translations.EnumerateObject())
            {
                if (language.Value.ValueKind != JsonValueKind.Object) continue;

                var sections = new List<MinutesSection>();

                if (language.Value.TryGetProperty("summary", out var overview) &&
                    overview.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(overview.GetString()))
                {
                    sections.Add(new MinutesSection
                    {
                        Key = "summary",
                        Kind = "paragraph",
                        Text = overview.GetString()
                    });
                }

                foreach (var property in language.Value.EnumerateObject())
                {
                    if (NonSectionKeys.Contains(property.Name)) continue;
                    if (property.Value.ValueKind != JsonValueKind.Array) continue;

                    var items = ReadItems(property.Value);
                    if (items.Count == 0) continue;

                    sections.Add(new MinutesSection
                    {
                        Key = property.Name,
                        Kind = "items",
                        Items = items
                    });
                }

                if (sections.Count > 0)
                {
                    result[language.Name] = sections;
                }
            }

            return result.Count > 0 ? result : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a section's array in either shape it has ever been written in: objects carrying
    /// <c>text</c>/<c>task</c> with a citation, and the bare strings that predate citations.
    /// </summary>
    private static List<MinutesItem> ReadItems(JsonElement array)
    {
        var items = new List<MinutesItem>();

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var value = entry.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    items.Add(new MinutesItem { Text = value! });
                }
                continue;
            }

            if (entry.ValueKind != JsonValueKind.Object) continue;

            var text = ReadString(entry, "text") ?? ReadString(entry, "task");
            if (string.IsNullOrWhiteSpace(text)) continue;

            items.Add(new MinutesItem
            {
                Text = text!,
                Owner = ReadString(entry, "owner"),
                AtMs = entry.TryGetProperty("atMs", out var atMs) && atMs.ValueKind == JsonValueKind.Number
                    ? atMs.GetInt64()
                    : null
            });
        }

        return items;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
