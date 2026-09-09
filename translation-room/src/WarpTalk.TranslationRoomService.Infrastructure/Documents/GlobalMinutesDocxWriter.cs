using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.Infrastructure.Documents;

/// <summary>
/// A biên bản as a Word document in international corporate form —
/// <see cref="MinutesTemplates.GlobalEn"/>, and the default.
///
/// The other half of the pair with <see cref="MeetingMinutesDocxWriter"/>. Same content, different
/// page. A reader holding both files must find the same record: nothing here is computed that the
/// Vietnamese layout does not also print, and nothing there is dropped here.
///
/// WHAT MAKES THIS THE GLOBAL FORM RATHER THAN A TRANSLATION
///     Three things, none of them the language:
///
///     DOCUMENT CONTROL. ISO 15489 asks a record to identify itself — id, version, status, who
///     owns it, when it was made — in a block a reader checks before reading the body. The
///     Vietnamese form carries the same facts scattered through the heading and the signature
///     block, which is where a Vietnamese reader looks for them. Here they are a table.
///
///     DECIMAL NUMBERING. 1, 1.1, 4.2 — so a reader can cite "section 4.2" in an email and the
///     recipient finds it. Roman numerals do not subdivide, which is why the Vietnamese layout
///     puts everything under one level.
///
///     MOTIONS, NOT TALLIES. Robert's Rules minutes record the action taken: who moved, who
///     seconded, and how it carried. The three counts alone answer "what was the vote" but not
///     "what was decided and on whose motion", which is the question this form is read for.
///
/// SAME REFUSAL TO INVENT
///     Every timestamp comes from the record; a missing one prints as a blank, never as today.
///     A motion's mover, seconder and outcome print only when the secretary stated them — this
///     class does NOT derive "carried" from the counts, because the majority a motion needed is
///     not something the service knows.
/// </summary>
public class GlobalMinutesDocxWriter
{
    /// <summary>The blank a printed form leaves for something nobody has filled in.</summary>
    private const string Blank = "—————————";

    /// <summary>
    /// Calibri rather than Aptos, which is the other half of the intended pair. Aptos ships with
    /// current Microsoft 365 and with nothing else: a document naming a font the reader does not
    /// have is re-flowed by whatever their Word picks instead, so the layout is only stable if
    /// the font is one that is actually everywhere. Calibri is.
    /// </summary>
    private const string BodyFont = "Calibri";

    /// <summary>Half-points. 22 = 11pt.</summary>
    private const int BodySize = 22;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public byte[] WriteDocx(MeetingMinutesDto minutes, MeetingMinutesContent content)
    {
        using var stream = new MemoryStream();

        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document();
            var body = main.Document.AppendChild(new Body());

            WriteHeading(body, minutes, content);
            WriteDocumentControl(body, minutes, content);
            WriteAttendance(body, content);
            WriteAgenda(body, content);
            WriteProceedings(body, content);
            WriteMotions(body, content);
            WriteAdjournment(body, content);
            WriteSignatures(body, minutes);

            // Last child of the body, which is where Word looks for it. A4 with 1 inch margins.
            body.AppendChild(PageSetup());

            main.Document.Save();
        }

        return stream.ToArray();
    }

    // ------------------------------------------------------------------ blocks

    private static void WriteHeading(Body body, MeetingMinutesDto minutes, MeetingMinutesContent content)
    {
        body.AppendChild(Line("MINUTES OF MEETING", size: 32, bold: true, alignment: JustificationValues.Center));

        if (!string.IsNullOrWhiteSpace(content.MeetingTitle))
        {
            body.AppendChild(Line(content.MeetingTitle!, size: 24, alignment: JustificationValues.Center));
        }

        // On the face of the document, not only in a database column. A draft that prints looking
        // exactly like an approved record is how an unapproved one gets circulated.
        body.AppendChild(Line(StatusLine(minutes), size: 18, italic: true, alignment: JustificationValues.Center));
        body.AppendChild(Spacer());
    }

    private static string StatusLine(MeetingMinutesDto minutes) => minutes.Status switch
    {
        "APPROVED" => minutes.Version > 1
            ? $"APPROVED — revision {minutes.Version - 1}"
            : "APPROVED",
        "IN_REVIEW" => "SIGNED BY THE SECRETARY — awaiting the chair's approval",
        _ => "DRAFT — unsigned and not approved"
    };

    /// <summary>
    /// The ISO 15489 identity block: what this document is, before what it says.
    ///
    /// A row is omitted entirely when the record has no value for it. A control table listing
    /// "Approved by: —" beside a document whose status is DRAFT states the same thing twice; one
    /// listing a field this service cannot know would be inviting a reader to treat a blank as
    /// meaningful.
    /// </summary>
    private static void WriteDocumentControl(Body body, MeetingMinutesDto minutes, MeetingMinutesContent content)
    {
        var rows = new List<(string Label, string? Value)>
        {
            ("Document ID", minutes.MinutesNo),
            ("Version", minutes.Version.ToString(Invariant)),
            ("Status", minutes.Status),
            ("Meeting opened", Moment(content.OpenedAt)),
            ("Meeting closed", Moment(content.ClosedAt)),
            ("Scheduled for", content.ScheduledAt.HasValue ? Moment(content.ScheduledAt) : null),
            ("Location", content.Location),
            ("Language of record", content.PrimaryLanguage),
            ("Drafted by", minutes.DraftedByEngine),
            ("Drafted at", minutes.DraftedAt.HasValue ? Moment(minutes.DraftedAt) : null),
            ("Secretary", minutes.SecretaryName),
            ("Signed at", minutes.SecretarySignedAt.HasValue ? Moment(minutes.SecretarySignedAt) : null),
            ("Chair", minutes.ChairName),
            ("Approved at", minutes.ChairApprovedAt.HasValue ? Moment(minutes.ChairApprovedAt) : null),
        };

        var table = new Table(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }),
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }));

        foreach (var (label, value) in rows)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            table.AppendChild(new TableRow(
                ControlCell(label, "1800", bold: true),
                ControlCell(value!, "3200")));
        }

        body.AppendChild(table);
        body.AppendChild(Spacer());
    }

    private static TableCell ControlCell(string text, string width, bool bold = false)
    {
        var cell = new TableCell(new TableCellProperties(
            new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = width }));
        cell.AppendChild(Line(text, size: 20, bold: bold));
        return cell;
    }

    private static void WriteAttendance(Body body, MeetingMinutesContent content)
    {
        var attendance = content.Attendance;

        body.AppendChild(Heading("1.", "Attendance"));

        var chair = attendance.Present.FirstOrDefault(
            person => string.Equals(person.Role, "HOST", StringComparison.OrdinalIgnoreCase));
        body.AppendChild(SubHeading("1.1", "In the chair"));
        body.AppendChild(Line(chair?.Name ?? Blank, indent: true));

        body.AppendChild(SubHeading("1.2", "Present"));
        if (attendance.Present.Count == 0)
        {
            body.AppendChild(Line("No attendance was recorded.", indent: true));
        }
        else
        {
            foreach (var person in attendance.Present)
            {
                var notes = new List<string>();
                if (person.IsExternal) notes.Add("external guest");
                if (!string.IsNullOrWhiteSpace(person.SpeakLanguage)) notes.Add($"spoke {person.SpeakLanguage}");
                if (person.JoinedAt.HasValue) notes.Add($"joined {Moment(person.JoinedAt)}");

                var suffix = notes.Count > 0 ? $" ({string.Join("; ", notes)})" : string.Empty;
                body.AppendChild(Line($"• {person.Name}{suffix}", indent: true));
            }
        }

        if (attendance.Absent.Count > 0)
        {
            body.AppendChild(SubHeading("1.3", "Apologies and absences"));
            foreach (var person in attendance.Absent)
            {
                var reason = string.IsNullOrWhiteSpace(person.Reason) ? Blank : person.Reason!;
                body.AppendChild(Line($"• {person.Name} — {reason}", indent: true));
            }
        }

        // Printed with the rule beside it. Quorum is the line that gets disputed later, and
        // "quorum met" on its own does not say what bar was applied.
        if (attendance.QuorumMet.HasValue)
        {
            var number = attendance.Absent.Count > 0 ? "1.4" : "1.3";
            body.AppendChild(SubHeading(number, "Quorum"));

            var verdict = attendance.QuorumMet.Value ? "quorum was met" : "QUORUM WAS NOT MET";
            var rule = string.IsNullOrWhiteSpace(attendance.QuorumRule)
                ? string.Empty
                : $" (rule applied: {attendance.QuorumRule})";
            body.AppendChild(Line(
                $"{attendance.PresentCount} of {attendance.InvitedCount} invited were present: {verdict}{rule}.",
                indent: true));
        }

        body.AppendChild(Spacer());
    }

    private static void WriteAgenda(Body body, MeetingMinutesContent content)
    {
        body.AppendChild(Heading("2.", "Agenda"));
        body.AppendChild(Line(
            string.IsNullOrWhiteSpace(content.Agenda) ? Blank : content.Agenda!,
            indent: true));
        body.AppendChild(Spacer());
    }

    private static void WriteProceedings(Body body, MeetingMinutesContent content)
    {
        body.AppendChild(Heading("3.", "Proceedings"));

        if (content.Sections.Count == 0)
        {
            body.AppendChild(Line(Blank, indent: true));
            body.AppendChild(Spacer());
            return;
        }

        var languages = content.Translations?.Keys.OrderBy(code => code, StringComparer.Ordinal).ToList()
            ?? new List<string>();

        var index = 0;
        foreach (var section in content.Sections)
        {
            index++;
            body.AppendChild(SubHeading($"3.{index}", SectionTitle(section.Key)));

            if (string.Equals(section.Kind, "paragraph", StringComparison.Ordinal))
            {
                body.AppendChild(Line(section.Text ?? Blank, indent: true));
                foreach (var language in languages)
                {
                    var translated = MinutesBilingualPairing.CounterpartOf(
                        section, content.Translations![language]);
                    if (!string.IsNullOrWhiteSpace(translated?.Text))
                    {
                        body.AppendChild(TranslatedLine(language, translated!.Text!));
                    }
                }
                continue;
            }

            var items = section.Items ?? new List<MinutesItem>();

            // One language pairs line-by-line, the rest print as blocks — the same rule the
            // Vietnamese layout applies, for the same reason: interleaving several languages under
            // every line turns a decision into a wall.
            var paired = languages
                .Select(language => new
                {
                    Language = language,
                    Pairs = MinutesBilingualPairing.PairByCitation(
                        items,
                        MinutesBilingualPairing.CounterpartOf(section, content.Translations![language])?.Items)
                })
                .FirstOrDefault(candidate => candidate.Pairs != null);

            if (paired != null)
            {
                foreach (var pair in paired.Pairs!)
                {
                    body.AppendChild(Line(OriginalLine(pair.Original), indent: true));
                    body.AppendChild(TranslatedLine(paired.Language, pair.Translated.Text));
                }
            }
            else
            {
                foreach (var item in items)
                {
                    body.AppendChild(Line(OriginalLine(item), indent: true));
                }
            }

            foreach (var language in languages)
            {
                if (paired != null && language == paired.Language) continue;

                var translated = MinutesBilingualPairing.CounterpartOf(
                    section, content.Translations![language]);
                if (translated?.Items == null || translated.Items.Count == 0) continue;

                body.AppendChild(Line($"[{language}]", size: 18, italic: true, indent: true));
                foreach (var item in translated.Items)
                {
                    body.AppendChild(TranslatedLine(language, item.Text, withPrefix: false));
                }
            }
        }

        body.AppendChild(Spacer());
    }

    /// <summary>
    /// Motions and how they were disposed of.
    ///
    /// Omitted entirely when nothing was moved, rather than printed as an empty heading — a blank
    /// "4. Motions and resolutions" invites a reader to read a decision into the gap.
    /// </summary>
    private static void WriteMotions(Body body, MeetingMinutesContent content)
    {
        if (content.Votes.Count == 0) return;

        body.AppendChild(Heading("4.", "Motions and resolutions"));

        var index = 0;
        foreach (var vote in content.Votes)
        {
            index++;
            body.AppendChild(SubHeading($"4.{index}", TopicOrBlank(vote)));

            // Who put the motion, in the sentence Robert's Rules reads it as. Each half is
            // printed only when it is known: "seconded by —————" would read as a motion that
            // failed for want of a seconder, which is a different fact.
            var attribution = new List<string>();
            if (!string.IsNullOrWhiteSpace(vote.MovedBy)) attribution.Add($"Moved by {vote.MovedBy}");
            if (!string.IsNullOrWhiteSpace(vote.SecondedBy)) attribution.Add($"seconded by {vote.SecondedBy}");
            if (attribution.Count > 0)
            {
                body.AppendChild(Line($"{string.Join(", ", attribution)}.", indent: true));
            }

            body.AppendChild(Line(
                $"In favour {vote.ForCount}; against {vote.AgainstCount}; abstaining {vote.AbstainCount}.",
                indent: true));

            // Never derived from the counts above. A motion needing a two-thirds majority carries
            // on numbers that would fail a simple one, and nothing here knows which bar applied.
            if (!string.IsNullOrWhiteSpace(vote.Outcome))
            {
                body.AppendChild(Line($"Outcome: {vote.Outcome}", indent: true, bold: true));
            }

            if (vote.AtMs.HasValue)
            {
                body.AppendChild(Line($"Recording reference: {Offset(vote.AtMs.Value)}", size: 18, italic: true, indent: true));
            }
        }

        body.AppendChild(Spacer());
    }

    private static void WriteAdjournment(Body body, MeetingMinutesContent content)
    {
        body.AppendChild(Heading("5.", "Adjournment"));

        var closed = content.ClosedAt.HasValue ? Moment(content.ClosedAt) : Blank;
        body.AppendChild(Line($"There being no further business, the meeting was adjourned at {closed}.", indent: true));

        if (!string.IsNullOrWhiteSpace(content.Notes))
        {
            body.AppendChild(SubHeading("5.1", "Secretary's notes"));
            body.AppendChild(Line(content.Notes!, indent: true));
        }

        body.AppendChild(Spacer());
    }

    private static void WriteSignatures(Body body, MeetingMinutesDto minutes)
    {
        body.AppendChild(Heading("6.", "Certification"));

        // Stated before the names. A reader must not have to infer that a program wrote the first
        // version of what they have just read.
        var drafted = minutes.DraftedAt.HasValue ? $" at {Moment(minutes.DraftedAt)}" : string.Empty;
        body.AppendChild(Line(
            $"The first draft of this record was produced by {minutes.DraftedByEngine ?? "the system"}{drafted}. "
            + "The secretary reviewed it and is answerable for its content.",
            size: 18,
            italic: true,
            indent: true));

        // The number a reader uses to judge whether anybody actually read the draft.
        if (minutes.SecretarySignedAt.HasValue)
        {
            var edits = minutes.EditCountVsDraft > 0
                ? $"The secretary made {minutes.EditCountVsDraft} change(s) to the draft before signing."
                : "The secretary signed the draft unchanged.";
            body.AppendChild(Line(edits, size: 18, italic: true, indent: true));
        }

        body.AppendChild(Spacer());

        var table = new Table(
            new TableProperties(new TableBorders(
                new TopBorder { Val = BorderValues.None },
                new BottomBorder { Val = BorderValues.None },
                new LeftBorder { Val = BorderValues.None },
                new RightBorder { Val = BorderValues.None },
                new InsideHorizontalBorder { Val = BorderValues.None },
                new InsideVerticalBorder { Val = BorderValues.None })),
            new TableRow(
                SignatureCell("SECRETARY", minutes.SecretaryName, minutes.SecretarySignedAt),
                SignatureCell("CHAIR", minutes.ChairName, minutes.ChairApprovedAt)));

        body.AppendChild(table);
    }

    private static TableCell SignatureCell(string role, string? name, DateTime? signedAt)
    {
        var cell = new TableCell(
            new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Pct, Width = "2500" }));

        cell.AppendChild(Line(role, size: 22, bold: true, alignment: JustificationValues.Center));
        cell.AppendChild(Line(
            signedAt.HasValue ? $"(signed {Moment(signedAt)})" : "(not signed)",
            size: 18,
            italic: true,
            alignment: JustificationValues.Center));
        // The empty lines a wet signature needs on a printed copy.
        cell.AppendChild(Spacer());
        cell.AppendChild(Spacer());
        cell.AppendChild(Line(
            string.IsNullOrWhiteSpace(name) ? Blank : name!,
            size: 22,
            bold: true,
            alignment: JustificationValues.Center));

        return cell;
    }

    // ------------------------------------------------------------------ primitives

    /// <summary>A4 portrait, 1 inch (1440 twip) margins all round.</summary>
    private static SectionProperties PageSetup() => new(
        new PageSize { Width = 11906U, Height = 16838U },
        new PageMargin
        {
            Top = 1440,
            Bottom = 1440,
            Left = 1440U,
            Right = 1440U,
            Header = 720U,
            Footer = 720U,
            Gutter = 0U
        });

    private static Paragraph Heading(string number, string title) =>
        Line($"{number} {title.ToUpperInvariant()}", size: 24, bold: true);

    private static Paragraph SubHeading(string number, string title) =>
        Line($"{number} {title}", size: 22, bold: true, indent: true);

    /// <summary>An original line: the words, who owns it, and the moment it came from.</summary>
    private static string OriginalLine(MinutesItem item)
    {
        var owner = string.IsNullOrWhiteSpace(item.Owner) ? string.Empty : $" — {item.Owner}";
        // Carried onto the printed page: it is what lets a reader of the paper copy go back to
        // the recording and check a line somebody signed for.
        var citation = item.AtMs.HasValue ? $" [{Offset(item.AtMs.Value)}]" : string.Empty;
        return $"• {item.Text}{owner}{citation}";
    }

    /// <summary>
    /// A translated line, visibly subordinate to the original — indented further, smaller, italic.
    /// A translation typeset identically to the original is one somebody will later quote as the
    /// original.
    /// </summary>
    private static Paragraph TranslatedLine(string language, string text, bool withPrefix = true)
    {
        var prefix = withPrefix ? $"[{language}] " : "  ";
        var paragraph = Line($"{prefix}{text}", size: 20, italic: true);
        paragraph.ParagraphProperties?.AppendChild(new Indentation { Left = "1080" });
        return paragraph;
    }

    private static Paragraph Line(
        string text,
        int size = BodySize,
        bool bold = false,
        bool italic = false,
        bool indent = false,
        JustificationValues? alignment = null)
    {
        var runProperties = new RunProperties(
            new RunFonts { Ascii = BodyFont, HighAnsi = BodyFont, ComplexScript = BodyFont },
            new FontSize { Val = size.ToString(Invariant) });
        if (bold) runProperties.AppendChild(new Bold());
        if (italic) runProperties.AppendChild(new Italic());

        var paragraphProperties = new ParagraphProperties();
        if (alignment.HasValue) paragraphProperties.AppendChild(new Justification { Val = alignment.Value });
        if (indent) paragraphProperties.AppendChild(new Indentation { Left = "360" });

        var run = new Run(runProperties);
        // Split on newlines rather than emitting them raw: a bare \n in a Word run is not a line
        // break, it is nothing, so a multi-line agenda would arrive as one run-on sentence.
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0) run.AppendChild(new Break());
            run.AppendChild(new Text(lines[index]) { Space = SpaceProcessingModeValues.Preserve });
        }

        return new Paragraph(paragraphProperties, run);
    }

    private static Paragraph Spacer() => new(new Run(new Text(string.Empty)));

    /// <summary>
    /// ISO 8601, in UTC, with the offset written out. Every timestamp this service holds is UTC,
    /// and a bare "2026-09-07 14:30" in an international document is ambiguous by exactly the
    /// amount that matters when a deadline is being read off it.
    /// </summary>
    private static string Moment(DateTime? value)
    {
        if (!value.HasValue) return Blank;
        var utc = DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z' (UTC+00:00)", Invariant);
    }

    /// <summary>A motion with no topic still gets a line; the blank shows something is missing.</summary>
    private static string TopicOrBlank(MinutesVote vote) =>
        string.IsNullOrWhiteSpace(vote.Topic) ? Blank : vote.Topic;

    private static string Offset(long atMs)
    {
        var total = Math.Max(atMs, 0) / 1000;
        return $"{total / 60:D2}:{total % 60:D2}";
    }

    /// <summary>
    /// Section headings, mirroring warptalk-ai's summary_templates. Unknown keys fall back to the
    /// key itself rather than being dropped — a template gaining a section must not silently lose
    /// its content out of the printed record.
    /// </summary>
    private static string SectionTitle(string key) => key switch
    {
        "carriedOver" => "Matters carried over",
        "summary" => "Summary",
        "decisions" => "Decisions",
        "actionItems" => "Action items",
        "openQuestions" => "Open questions",
        "progress" => "Progress",
        "plans" => "Plans",
        "blockers" => "Blockers",
        "background" => "Background",
        "strengths" => "Strengths",
        "concerns" => "Concerns",
        "shown" => "Presented",
        "reactions" => "Reactions",
        "objections" => "Objections",
        "problems" => "Problems raised",
        "options" => "Options",
        _ => key
    };
}
