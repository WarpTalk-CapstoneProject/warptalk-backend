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
///     owns it, when it was made, AND WHAT IT IS A RECORD OF — in a block a reader checks before
///     reading the body. The Vietnamese form carries the same facts scattered through the heading
///     and the signature block, which is where a Vietnamese reader looks for them. Here they are
///     a table.
///
///     DECIMAL NUMBERING. 1, 1.1, 4.2 — so a reader can cite "section 4.2" in an email and the
///     recipient finds it. Roman numerals do not subdivide, which is why the Vietnamese layout
///     puts everything under one level. The numbers are COUNTED, not written in: a document that
///     skips from 3 to 5 because nothing was moved reads as one with a page missing.
///
///     MOTIONS, NOT TALLIES. Robert's Rules minutes record the action taken: who moved, who
///     seconded, and how it carried. The three counts alone answer "what was the vote" but not
///     "what was decided and on whose motion", which is the question this form is read for.
///
/// THE CERTIFICATION BLOCK IS THE INTERNATIONAL ONE, NOT THE VIETNAMESE ONE TRANSLATED
///     A Vietnamese signature block puts the role above and the name below the space for the
///     signature. The international convention runs the other way and is four fixed parts in a
///     fixed order: a signature rule, the printed name, the capacity the person signs in, and the
///     date. Above them sits the sentence the signature attests to — "a true and correct record" —
///     because a signature under nothing in particular certifies nothing in particular.
///
/// SAME REFUSAL TO INVENT
///     Every timestamp comes from the record; a missing one prints as a blank, never as today.
///     A motion's mover, seconder and outcome print only when the secretary stated them — this
///     class does NOT derive "carried" from the counts, because the majority a motion needed is
///     not something the service knows. The adjournment line records the hour and nothing else:
///     "there being no further business" is a claim about a room this service was not in.
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

    private const int SmallSize = 18;

    /// <summary>One indent step, in twips, and the hanging indent a list item wraps to.</summary>
    private const int Indent = 360;

    /// <summary>Line spacing in twentieths of a line: 1.15, the international document default.</summary>
    private const string LineSpacing = "276";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public byte[] WriteDocx(MeetingMinutesDto minutes, MeetingMinutesContent content)
    {
        using var stream = new MemoryStream();

        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document();
            WriteStyles(main);

            var body = main.Document.AppendChild(new Body());

            // Counted rather than written in, so a document with no motions runs 1..5 instead of
            // skipping 4.
            var numbering = new DecimalCounter();

            WriteHeading(body, minutes, content);
            WriteDocumentControl(body, minutes, content);
            WriteAttendance(body, content, numbering);
            WriteAgenda(body, content, numbering);
            WriteProceedings(body, content, numbering);
            WriteMotions(body, content, numbering);
            WriteAdjournment(body, content, numbering);
            WriteCertification(body, minutes, content, numbering);

            // Last child of the body, which is where Word looks for it. A4 with 1 inch margins.
            body.AppendChild(PageSetup(main, minutes));

            main.Document.Save();
        }

        return stream.ToArray();
    }

    // ------------------------------------------------------------------ page and styles

    /// <summary>
    /// The document's defaults, set once rather than on every run, so that text somebody types
    /// into the exported file inherits the same face and size as the text this class wrote.
    /// </summary>
    private static void WriteStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(
                    new RunPropertiesBaseStyle(
                        new RunFonts { Ascii = BodyFont, HighAnsi = BodyFont, ComplexScript = BodyFont },
                        new FontSize { Val = BodySize.ToString(Invariant) },
                        new FontSizeComplexScript { Val = BodySize.ToString(Invariant) })),
                new ParagraphPropertiesDefault(
                    new ParagraphPropertiesBaseStyle(
                        new SpacingBetweenLines
                        {
                            After = "120",
                            Line = LineSpacing,
                            LineRule = LineSpacingRuleValues.Auto
                        },
                        // Left, not justified: justification is a Vietnamese convention, and an
                        // English page set that way opens the rivers of white space that make a
                        // reader think the file was produced badly.
                        new Justification { Val = JustificationValues.Left }))));
        part.Styles.Save();
    }

    /// <summary>A4 portrait, 1 inch (1440 twip) margins all round, and a footer that identifies the page.</summary>
    private static SectionProperties PageSetup(MainDocumentPart main, MeetingMinutesDto minutes)
    {
        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = PageFooter(minutes);
        footer.Footer.Save();

        return new SectionProperties(
            new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) },
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
    }

    /// <summary>
    /// "BB-2026-0007 — Page 1 of 3".
    ///
    /// Both halves earn their place on a controlled document: the page count is how a reader
    /// notices a missing sheet, and the document id is how a sheet that got separated from the
    /// rest is put back with the right file.
    /// </summary>
    private static Footer PageFooter(MeetingMinutesDto minutes)
    {
        var paragraph = Shell(SmallSize, JustificationValues.Center, spaceAfter: 0);

        if (!string.IsNullOrWhiteSpace(minutes.MinutesNo))
        {
            paragraph.AppendChild(TextRun($"{minutes.MinutesNo} — ", SmallSize, italic: true));
        }

        paragraph.AppendChild(TextRun("Page ", SmallSize, italic: true));
        paragraph.AppendChild(Field("PAGE"));
        paragraph.AppendChild(TextRun(" of ", SmallSize, italic: true));
        paragraph.AppendChild(Field("NUMPAGES"));

        return new Footer(paragraph);
    }

    private static SimpleField Field(string instruction) =>
        new(new Run(RunStyle(SmallSize, italic: true), new Text("1"))) { Instruction = instruction };

    // ------------------------------------------------------------------ blocks

    private static void WriteHeading(Body body, MeetingMinutesDto minutes, MeetingMinutesContent content)
    {
        body.AppendChild(Line(
            "MINUTES OF MEETING",
            size: 32,
            bold: true,
            alignment: JustificationValues.Center,
            spaceAfter: 60,
            keepNext: true));

        if (!string.IsNullOrWhiteSpace(content.MeetingTitle))
        {
            body.AppendChild(Line(
                content.MeetingTitle!,
                size: 24,
                alignment: JustificationValues.Center,
                spaceAfter: 0,
                keepNext: true));
        }

        // On the face of the document, not only in a database column. A draft that prints looking
        // exactly like an approved record is how an unapproved one gets circulated.
        body.AppendChild(Line(
            StatusLine(minutes),
            size: SmallSize,
            italic: true,
            alignment: JustificationValues.Center,
            spaceAfter: 240));
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
    ///
    /// "Source record" and "Translations" belong here rather than in the body because they answer
    /// the question this block exists for — what is this a record OF — and because a reader
    /// deciding how much weight to give a line needs to know BEFORE reading it whether they are
    /// looking at what was said or at a machine's rendering of it.
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
            ("Language of record", LanguageOfRecord(content)),
            ("Translations", TranslationsNote(content)),
            ("Source record", SourceRecord(minutes)),
            ("Drafted by", minutes.DraftedByEngine),
            ("Drafted at", minutes.DraftedAt.HasValue ? Moment(minutes.DraftedAt) : null),
            ("Secretary", minutes.SecretaryName),
            ("Signed at", minutes.SecretarySignedAt.HasValue ? Moment(minutes.SecretarySignedAt) : null),
            ("Chair", minutes.ChairName),
            ("Approved at", minutes.ChairApprovedAt.HasValue ? Moment(minutes.ChairApprovedAt) : null),
        };

        var table = new Table(new TableProperties(
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));

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

    /// <summary>The language the meeting was held in — the original, as against any rendering of it.</summary>
    private static string? LanguageOfRecord(MeetingMinutesContent content) =>
        string.IsNullOrWhiteSpace(content.PrimaryLanguage)
            ? null
            : $"{content.PrimaryLanguage} (the language the meeting was held in)";

    /// <summary>
    /// How many languages this record carries besides the original, and what they are worth.
    ///
    /// Said in the control block because a translation printed beside an original without being
    /// called a translation is how a machine rendering ends up quoted as somebody's words.
    /// </summary>
    private static string? TranslationsNote(MeetingMinutesContent content)
    {
        var languages = content.Translations?.Keys.OrderBy(code => code, StringComparer.Ordinal).ToList();
        if (languages == null || languages.Count == 0) return null;

        return $"{languages.Count} ({string.Join(", ", languages)}) — machine-translated from the "
            + "original; not the words the participants spoke";
    }

    /// <summary>
    /// What the draft was made from. Printed only when a program drafted it: minutes typed by a
    /// person have no transcript behind them, and claiming one would be an invented provenance.
    /// </summary>
    private static string? SourceRecord(MeetingMinutesDto minutes)
    {
        if (string.IsNullOrWhiteSpace(minutes.DraftedByEngine)) return null;

        var version = minutes.BasedOnTranscriptVersion.HasValue
            ? $" (version {minutes.BasedOnTranscriptVersion.Value})"
            : string.Empty;

        return $"Transcript of this meeting{version}; the [mm:ss] references in the body point into it";
    }

    private static TableCell ControlCell(string text, string width, bool bold = false)
    {
        var cell = new TableCell(new TableCellProperties(
            new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = width }));
        cell.AppendChild(Line(text, size: 20, bold: bold, spaceAfter: 0));
        return cell;
    }

    private static void WriteAttendance(Body body, MeetingMinutesContent content, DecimalCounter numbering)
    {
        var attendance = content.Attendance;

        body.AppendChild(Heading(numbering.NextSection(), "Attendance"));

        var chair = attendance.Present.FirstOrDefault(
            person => string.Equals(person.Role, "HOST", StringComparison.OrdinalIgnoreCase));
        body.AppendChild(SubHeading(numbering.NextSub(), "In the chair"));
        body.AppendChild(Line(chair?.Name ?? Blank, left: Indent));

        body.AppendChild(SubHeading(numbering.NextSub(), "Present"));
        if (attendance.Present.Count == 0)
        {
            body.AppendChild(Line("No attendance was recorded.", left: Indent));
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
                body.AppendChild(Bullet($"{person.Name}{suffix}"));
            }
        }

        if (attendance.Absent.Count > 0)
        {
            body.AppendChild(SubHeading(numbering.NextSub(), "Apologies and absences"));
            foreach (var person in attendance.Absent)
            {
                var reason = string.IsNullOrWhiteSpace(person.Reason) ? Blank : person.Reason!;
                body.AppendChild(Bullet($"{person.Name} — {reason}"));
            }
        }

        // Printed with the rule beside it. Quorum is the line that gets disputed later, and
        // "quorum met" on its own does not say what bar was applied.
        if (attendance.QuorumMet.HasValue)
        {
            body.AppendChild(SubHeading(numbering.NextSub(), "Quorum"));

            var verdict = attendance.QuorumMet.Value ? "quorum was met" : "QUORUM WAS NOT MET";
            var rule = string.IsNullOrWhiteSpace(attendance.QuorumRule)
                ? string.Empty
                : $" (rule applied: {attendance.QuorumRule})";
            body.AppendChild(Line(
                $"{attendance.PresentCount} of {attendance.InvitedCount} invited were present: {verdict}{rule}.",
                left: Indent));
        }
    }

    private static void WriteAgenda(Body body, MeetingMinutesContent content, DecimalCounter numbering)
    {
        body.AppendChild(Heading(numbering.NextSection(), "Agenda"));
        WriteRichText(body, content.Agenda);

        // The blank on its own reads as a bug in the exporter. Saying why it is blank turns it
        // back into what it is: a meeting booked without a programme, and a line for a person.
        if (string.IsNullOrWhiteSpace(content.Agenda))
        {
            body.AppendChild(Line(
                "(No agenda was circulated before the meeting; the secretary completes this section.)",
                size: SmallSize,
                italic: true,
                left: Indent));
        }
    }

    private static void WriteProceedings(Body body, MeetingMinutesContent content, DecimalCounter numbering)
    {
        body.AppendChild(Heading(numbering.NextSection(), "Proceedings"));

        if (content.Sections.Count == 0)
        {
            body.AppendChild(Line(Blank, left: Indent));
            return;
        }

        var languages = content.Translations?.Keys.OrderBy(code => code, StringComparer.Ordinal).ToList()
            ?? new List<string>();

        foreach (var section in content.Sections)
        {
            body.AppendChild(SubHeading(numbering.NextSub(), SectionTitle(section.Key)));

            if (string.Equals(section.Kind, "paragraph", StringComparison.Ordinal))
            {
                WriteRichText(body, section.Text);
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
                    body.AppendChild(ItemLine(pair.Original));
                    body.AppendChild(TranslatedLine(paired.Language, pair.Translated.Text));
                }
            }
            else
            {
                foreach (var item in items)
                {
                    body.AppendChild(ItemLine(item));
                }
            }

            foreach (var language in languages)
            {
                if (paired != null && language == paired.Language) continue;

                var translated = MinutesBilingualPairing.CounterpartOf(
                    section, content.Translations![language]);
                if (translated?.Items == null || translated.Items.Count == 0) continue;

                body.AppendChild(Line($"[{language}]", size: SmallSize, italic: true, left: Indent));
                foreach (var item in translated.Items)
                {
                    body.AppendChild(TranslatedLine(language, item.Text, withPrefix: false));
                }
            }
        }
    }

    /// <summary>
    /// Motions and how they were disposed of.
    ///
    /// Omitted entirely when nothing was moved, rather than printed as an empty heading — a blank
    /// "Motions and resolutions" invites a reader to read a decision into the gap. The number comes
    /// from the counter, so omitting the section closes the gap instead of leaving one.
    /// </summary>
    private static void WriteMotions(Body body, MeetingMinutesContent content, DecimalCounter numbering)
    {
        if (content.Votes.Count == 0) return;

        body.AppendChild(Heading(numbering.NextSection(), "Motions and resolutions"));

        foreach (var vote in content.Votes)
        {
            body.AppendChild(SubHeading(numbering.NextSub(), TopicOrBlank(vote)));

            // Who put the motion, in the sentence Robert's Rules reads it as. Each half is
            // printed only when it is known: "seconded by —————" would read as a motion that
            // failed for want of a seconder, which is a different fact.
            var attribution = new List<string>();
            if (!string.IsNullOrWhiteSpace(vote.MovedBy)) attribution.Add($"Moved by {vote.MovedBy}");
            if (!string.IsNullOrWhiteSpace(vote.SecondedBy)) attribution.Add($"seconded by {vote.SecondedBy}");
            if (attribution.Count > 0)
            {
                body.AppendChild(Line($"{string.Join(", ", attribution)}.", left: Indent));
            }

            body.AppendChild(Line(
                $"In favour {vote.ForCount}; against {vote.AgainstCount}; abstaining {vote.AbstainCount}.",
                left: Indent));

            // Never derived from the counts above. A motion needing a two-thirds majority carries
            // on numbers that would fail a simple one, and nothing here knows which bar applied.
            if (!string.IsNullOrWhiteSpace(vote.Outcome))
            {
                body.AppendChild(Line($"Outcome: {vote.Outcome}", left: Indent, bold: true));
            }

            if (vote.AtMs.HasValue)
            {
                body.AppendChild(Line(
                    $"Recording reference: {Offset(vote.AtMs.Value)}",
                    size: SmallSize, italic: true, left: Indent));
            }
        }
    }

    private static void WriteAdjournment(Body body, MeetingMinutesContent content, DecimalCounter numbering)
    {
        body.AppendChild(Heading(numbering.NextSection(), "Adjournment"));

        // The hour, which is what Robert's Rules asks the minutes to record, and nothing else.
        // "There being no further business" is a claim about what happened in a room this service
        // was not in, and it would print identically on a meeting that simply ran out of time.
        var closed = content.ClosedAt.HasValue ? Moment(content.ClosedAt) : Blank;
        body.AppendChild(Line($"The meeting was adjourned at {closed}.", left: Indent));

        if (!string.IsNullOrWhiteSpace(content.Notes))
        {
            body.AppendChild(SubHeading(numbering.NextSub(), "Secretary's notes"));
            WriteRichText(body, content.Notes);
        }
    }

    /// <summary>
    /// The attestation, and the block that carries the signatures.
    ///
    /// THE SENTENCE COMES FIRST BECAUSE IT IS WHAT IS BEING SIGNED
    ///     "A true and correct record" is the standard certification wording, and a signature under
    ///     nothing in particular certifies nothing in particular. It is pre-printed on an unsigned
    ///     copy exactly as it would be on a paper form: the sentence is the claim, the signature is
    ///     what makes it a claim somebody has made, and the "(not signed)" marker under the rule is
    ///     what stops an unsigned draft reading as a certified record.
    ///
    /// THE BLOCK IS RULE → NAME → CAPACITY → DATE
    ///     The four parts of an international signature block, in the order a reader's eye expects
    ///     them, with blank lines above the rule for a wet signature. The Vietnamese form puts the
    ///     role above and the name below; doing that here would be the Vietnamese block with
    ///     English words in it, which is precisely what this template exists not to be.
    /// </summary>
    private static void WriteCertification(
        Body body, MeetingMinutesDto minutes, MeetingMinutesContent content, DecimalCounter numbering)
    {
        body.AppendChild(Heading(numbering.NextSection(), "Certification"));

        var heldOn = content.OpenedAt.HasValue
            ? $" of the meeting held on {content.OpenedAt.Value.ToString("yyyy-MM-dd", Invariant)}"
            : string.Empty;
        body.AppendChild(Line(
            $"We certify that the foregoing is a true and correct record{heldOn}.",
            left: Indent));

        // Stated before the names. A reader must not have to infer that a program wrote the first
        // version of what they have just read.
        var drafted = minutes.DraftedAt.HasValue ? $" at {Moment(minutes.DraftedAt)}" : string.Empty;
        body.AppendChild(Line(
            $"The first draft of this record was produced by {minutes.DraftedByEngine ?? "the system"}{drafted}. "
            + "The secretary reviewed it and is answerable for its content.",
            size: SmallSize,
            italic: true,
            left: Indent));

        // The number a reader uses to judge whether anybody actually read the draft.
        if (minutes.SecretarySignedAt.HasValue)
        {
            var edits = minutes.EditCountVsDraft > 0
                ? $"The secretary made {minutes.EditCountVsDraft} change(s) to the draft before signing."
                : "The secretary signed the draft unchanged.";
            body.AppendChild(Line(edits, size: SmallSize, italic: true, left: Indent));
        }

        body.AppendChild(Spacer());

        var table = new Table(
            new TableProperties(
                new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
                new TableBorders(
                    new TopBorder { Val = BorderValues.None },
                    new BottomBorder { Val = BorderValues.None },
                    new LeftBorder { Val = BorderValues.None },
                    new RightBorder { Val = BorderValues.None },
                    new InsideHorizontalBorder { Val = BorderValues.None },
                    new InsideVerticalBorder { Val = BorderValues.None })),
            new TableRow(
                // A signature block split across a page break is the first thing a reader
                // distrusts, so the two columns stay on one page together.
                new TableRowProperties(new CantSplit()),
                SignatureCell(minutes.SecretaryName, "Secretary", minutes.SecretarySignedAt),
                SignatureCell(minutes.ChairName, "Chair of the meeting", minutes.ChairApprovedAt)));

        body.AppendChild(table);
    }

    /// <summary>One signatory: room to sign, a rule, the printed name, the capacity, and the date.</summary>
    private static TableCell SignatureCell(string? name, string capacity, DateTime? signedAt)
    {
        var cell = new TableCell(
            new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Pct, Width = "2500" }));

        // The blank lines a wet signature needs on a printed copy.
        cell.AppendChild(Spacer());
        cell.AppendChild(Spacer());
        cell.AppendChild(Spacer());

        // The rule itself: a bottom border on an empty paragraph rather than a run of underscores,
        // which lands at a different width in every font the document might be re-flowed in.
        var rule = Shell(BodySize, spaceAfter: 0);
        rule.ParagraphProperties!.AppendChild(new ParagraphBorders(
            new BottomBorder { Val = BorderValues.Single, Size = 6, Space = 1 }));
        cell.AppendChild(rule);

        cell.AppendChild(Line(
            string.IsNullOrWhiteSpace(name) ? Blank : name!,
            bold: true,
            spaceBefore: 60,
            spaceAfter: 0));
        cell.AppendChild(Line(capacity, size: 20, spaceAfter: 0));
        // The calendar date, not the full timestamp: a signature block is read by a person
        // checking when it was signed, and the exact instant is already in the control table
        // above as "Signed at" / "Approved at".
        cell.AppendChild(Line(
            signedAt.HasValue
                ? $"Date: {signedAt.Value.ToString("yyyy-MM-dd", Invariant)}"
                : "Date: ————————————",
            size: 20,
            spaceAfter: 0));

        // Said in as many words rather than left to an empty date: an unsigned block that merely
        // looks incomplete is one somebody circulates anyway.
        if (!signedAt.HasValue)
        {
            cell.AppendChild(Line("(not signed)", size: SmallSize, italic: true));
        }

        return cell;
    }

    // ------------------------------------------------------------------ narrative

    /// <summary>
    /// Prose the model wrote, set as Word blocks instead of printed as source.
    ///
    /// Same reader as the Vietnamese form uses — <see cref="MinutesMarkdown"/> — and a different
    /// setting: this form leads its items with a bullet and indents them the way an English
    /// document does. Empty text prints the blank a printed form would have, never nothing.
    /// </summary>
    private static void WriteRichText(Body body, string? text, int left = Indent)
    {
        var blocks = MinutesMarkdown.Parse(text);
        if (blocks.Count == 0)
        {
            body.AppendChild(Line(Blank, left: left));
            return;
        }

        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case MinutesMarkdown.BlockKind.Heading:
                    body.AppendChild(RichLine(
                        block.Text, bold: true, left: left, spaceBefore: 120, spaceAfter: 40, keepNext: true));
                    break;

                case MinutesMarkdown.BlockKind.Item:
                    body.AppendChild(RichLine(
                        block.Text,
                        left: left + Indent + (block.Depth * Indent),
                        hanging: Indent,
                        // The model's own numbering is kept: a list renumbered by a renderer stops
                        // matching the sentence above it that referred to item 3.
                        prefix: block.Marker != null ? $"{block.Marker} " : "• "));
                    break;

                default:
                    body.AppendChild(RichLine(block.Text, left: left));
                    break;
            }
        }
    }

    /// <summary>An original line: the words, who owns it, and the moment it came from.</summary>
    private static Paragraph ItemLine(MinutesItem item)
    {
        var paragraph = Shell(BodySize, left: Indent * 2, hanging: Indent);
        paragraph.AppendChild(TextRun("• ", BodySize));
        AppendInline(paragraph, item.Text ?? string.Empty, BodySize);

        if (!string.IsNullOrWhiteSpace(item.Owner))
        {
            paragraph.AppendChild(TextRun($" — {item.Owner}", BodySize));
        }

        // Carried onto the printed page, set smaller so it reads as apparatus rather than as part
        // of the sentence: it is what lets a reader of the paper copy go back to the recording and
        // check a line somebody signed for.
        if (item.AtMs.HasValue)
        {
            paragraph.AppendChild(TextRun($" [{Offset(item.AtMs.Value)}]", SmallSize, italic: true));
        }

        return paragraph;
    }

    /// <summary>
    /// A translated line, visibly subordinate to the original — indented further, smaller, italic.
    /// A translation typeset identically to the original is one somebody will later quote as the
    /// original.
    /// </summary>
    private static Paragraph TranslatedLine(string language, string text, bool withPrefix = true)
    {
        var paragraph = Shell(20, left: Indent * 3, hanging: withPrefix ? Indent : 0);
        if (withPrefix) paragraph.AppendChild(TextRun($"[{language}] ", 20, italic: true));
        AppendInline(paragraph, text, 20, italic: true);
        return paragraph;
    }

    private static void AppendInline(Paragraph paragraph, string text, int size, bool bold = false, bool italic = false)
    {
        foreach (var span in MinutesMarkdown.InlineSpans(text))
        {
            paragraph.AppendChild(TextRun(span.Text, size, bold || span.Bold, italic || span.Italic));
        }
    }

    // ------------------------------------------------------------------ primitives

    /// <summary>
    /// Decimal section numbers, handed out in the order they print: 1, 1.1, 1.2, 2, 2.1…
    ///
    /// Counted rather than written in, so a section this document omits — motions nobody moved —
    /// closes the gap behind it instead of leaving one. A reader who sees 3 followed by 5
    /// reasonably concludes a page is missing.
    /// </summary>
    private sealed class DecimalCounter
    {
        private int _section;
        private int _sub;

        public string NextSection()
        {
            _section++;
            _sub = 0;
            return $"{_section.ToString(Invariant)}.";
        }

        public string NextSub()
        {
            _sub++;
            return $"{_section.ToString(Invariant)}.{_sub.ToString(Invariant)}";
        }
    }

    private static Paragraph Heading(string number, string title) => Line(
        $"{number} {title.ToUpperInvariant()}",
        size: 24,
        bold: true,
        spaceBefore: 240,
        spaceAfter: 100,
        keepNext: true);

    private static Paragraph SubHeading(string number, string title) => Line(
        $"{number} {title}",
        bold: true,
        left: Indent,
        spaceBefore: 140,
        spaceAfter: 60,
        keepNext: true);

    private static Paragraph Bullet(string text) =>
        RichLine(text, left: Indent * 2, hanging: Indent, prefix: "• ");

    /// <summary>An empty paragraph carrying the properties every paragraph in this document needs.</summary>
    private static Paragraph Shell(
        int size = BodySize,
        JustificationValues? alignment = null,
        int left = 0,
        int hanging = 0,
        int? spaceBefore = null,
        int? spaceAfter = null,
        bool keepNext = false)
    {
        // Child order follows the schema (keepNext → spacing → ind → jc); Word rejects properties
        // written out of order even though the API will happily build them.
        var properties = new ParagraphProperties();
        if (keepNext) properties.AppendChild(new KeepNext());

        var spacing = new SpacingBetweenLines { Line = LineSpacing, LineRule = LineSpacingRuleValues.Auto };
        if (spaceBefore.HasValue) spacing.Before = spaceBefore.Value.ToString(Invariant);
        if (spaceAfter.HasValue) spacing.After = spaceAfter.Value.ToString(Invariant);
        properties.AppendChild(spacing);

        if (left > 0 || hanging > 0)
        {
            var indentation = new Indentation();
            if (left > 0) indentation.Left = left.ToString(Invariant);
            // Hanging, not first-line: a wrapped list item aligns under its own text, not under
            // the bullet.
            if (hanging > 0) indentation.Hanging = hanging.ToString(Invariant);
            properties.AppendChild(indentation);
        }

        properties.AppendChild(new Justification { Val = alignment ?? JustificationValues.Left });

        return new Paragraph(properties);
    }

    private static RunProperties RunStyle(int size, bool bold = false, bool italic = false)
    {
        // Same schema-order rule as paragraphs: rFonts → b → i → sz.
        var properties = new RunProperties(
            new RunFonts { Ascii = BodyFont, HighAnsi = BodyFont, ComplexScript = BodyFont });
        if (bold) properties.AppendChild(new Bold());
        if (italic) properties.AppendChild(new Italic());
        properties.AppendChild(new FontSize { Val = size.ToString(Invariant) });
        properties.AppendChild(new FontSizeComplexScript { Val = size.ToString(Invariant) });
        return properties;
    }

    private static Run TextRun(string text, int size, bool bold = false, bool italic = false)
    {
        var run = new Run(RunStyle(size, bold, italic));

        // Split on newlines rather than emitting them raw: a bare \n in a Word run is not a line
        // break, it is nothing, so a multi-line note would arrive as one run-on sentence.
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0) run.AppendChild(new Break());
            run.AppendChild(new Text(lines[index]) { Space = SpaceProcessingModeValues.Preserve });
        }

        return run;
    }

    private static Paragraph Line(
        string text,
        int size = BodySize,
        bool bold = false,
        bool italic = false,
        JustificationValues? alignment = null,
        int left = 0,
        int hanging = 0,
        int? spaceBefore = null,
        int? spaceAfter = null,
        bool keepNext = false)
    {
        var paragraph = Shell(size, alignment, left, hanging, spaceBefore, spaceAfter, keepNext);
        paragraph.AppendChild(TextRun(text, size, bold, italic));
        return paragraph;
    }

    /// <summary>A line whose text may carry inline Markdown, optionally behind a list marker.</summary>
    private static Paragraph RichLine(
        string text,
        int size = BodySize,
        bool bold = false,
        bool italic = false,
        int left = 0,
        int hanging = 0,
        string? prefix = null,
        int? spaceBefore = null,
        int? spaceAfter = null,
        bool keepNext = false)
    {
        var paragraph = Shell(size, null, left, hanging, spaceBefore, spaceAfter, keepNext);
        if (prefix != null) paragraph.AppendChild(TextRun(prefix, size, bold, italic));
        AppendInline(paragraph, text, size, bold, italic);
        return paragraph;
    }

    private static Paragraph Spacer() => new(
        new ParagraphProperties(new SpacingBetweenLines { Before = "0", After = "0" }),
        new Run(RunStyle(SmallSize), new Text(string.Empty)));

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
