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
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.Infrastructure.Documents;

/// <summary>
/// A biên bản as a Word document, in the order Vietnamese practice reads it —
/// <see cref="MinutesTemplates.VnNd30"/>.
///
/// One of two layouts over the same content; <see cref="GlobalMinutesDocxWriter"/> is the other,
/// and <see cref="MinutesDocumentWriter"/> chooses between them. This class is not the fallback:
/// for a domestic filing it is the correct form, and a reader who expects it will reject anything
/// else.
///
/// THE ORDER IS THE SPEC
///     Đơn vị → số biên bản → tên biên bản → thời gian và địa điểm → thành phần tham dự → vắng mặt
///     → chương trình → nội dung → biểu quyết → kết luận về thời gian bế mạc → chữ ký. A reader
///     who has signed a hundred of these finds each part where they expect it; a document that
///     rearranges them reads as something else that happens to contain the same facts.
///
/// IT IS TYPESET, NOT JUST FILLED IN
///     A4, Times New Roman 13, lề trái 30mm / phải 20mm / trên–dưới 20mm, giãn dòng 1.3, thân bài
///     căn đều, đề mục đánh số La Mã — the vn-nd30 shape. Without a section and a style part Word
///     falls back to Calibri 11 on 1-inch margins, which is a memo, not a record somebody signs.
///     Page numbers go in the footer because a signed document that loses a page must show it.
///
/// THE NARRATIVE ARRIVES AS MARKDOWN
///     warptalk-ai writes the overview in Markdown — "## ", "### ", "- ", "**". Word has no idea
///     what those are, so printing the string raw puts hash marks and asterisks into a legal
///     record. <see cref="WriteRichText"/> renders them: headings become sub-headings, dashes
///     become indented bullets with a hanging indent, ** becomes bold. Markup is converted, never
///     stripped-and-dropped — the text a person will sign for stays complete.
///
/// NO DATES, AND NO EVENTS, THIS CLASS INVENTS
///     Every timestamp printed comes from the record. A missing one prints as "………" — the blank a
///     paper form would have — rather than as today's date, which is the single most dangerous
///     thing a document generator can do to a record with legal weight. For the same reason the
///     closing line only says the minutes were read back and adopted when they actually carry a
///     chair's approval.
/// </summary>
public class MeetingMinutesDocxWriter
{
    /// <summary>The blank a paper form leaves for something nobody has filled in.</summary>
    private const string Blank = "…………………";

    private const string BodyFont = "Times New Roman";

    // Half-points, the unit Word measures type in. 26 = 13pt, the size Nghị định 30 asks for.
    private const int BodySize = 26;
    private const int SmallSize = 22;
    private const int TitleSize = 32;

    /// <summary>One indent step, in twips. Also the hanging indent a bullet's text wraps to.</summary>
    private const int Indent = 360;

    /// <summary>Line spacing, in twentieths of a line: 1.3, which is what a signed page uses.</summary>
    private const string LineSpacing = "312";

    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");

    public byte[] WriteDocx(MeetingMinutesDto minutes, MeetingMinutesContent content)
    {
        using var stream = new MemoryStream();

        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document();
            WriteStyles(main);

            var body = main.Document.AppendChild(new Body());

            // The Roman numerals are counted, not hard-coded: BIỂU QUYẾT is omitted when nobody
            // voted, and a document that jumps from III to V reads as one with a page missing.
            var numbering = new RomanCounter();

            WriteHeading(body, minutes, content);
            WriteFacts(body, content);
            WriteProvenance(body, minutes, content);
            WriteAttendance(body, content, numbering);
            WriteAgenda(body, content, numbering);
            WriteSections(body, content, numbering);
            WriteVotes(body, content, numbering);
            WriteClosing(body, minutes, content, numbering);
            WriteSignatures(body, minutes);

            body.AppendChild(PageSetup(main));

            main.Document.Save();
        }

        return stream.ToArray();
    }

    // ------------------------------------------------------------------ page and styles

    /// <summary>
    /// The document's defaults. Set once here rather than on every run, so that text a secretary
    /// types into the exported file inherits the same face and size as the text this class wrote.
    /// </summary>
    private static void WriteStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(
                    new RunPropertiesBaseStyle(
                        new RunFonts { Ascii = BodyFont, HighAnsi = BodyFont, ComplexScript = BodyFont },
                        new FontSize { Val = BodySize.ToString(CultureInfo.InvariantCulture) },
                        new FontSizeComplexScript { Val = BodySize.ToString(CultureInfo.InvariantCulture) })),
                new ParagraphPropertiesDefault(
                    new ParagraphPropertiesBaseStyle(
                        new SpacingBetweenLines
                        {
                            After = "80",
                            Line = LineSpacing,
                            LineRule = LineSpacingRuleValues.Auto
                        },
                        new Justification { Val = JustificationValues.Both }))));
        part.Styles.Save();
    }

    /// <summary>A4 with the margins of a Vietnamese official document, and a numbered footer.</summary>
    private static SectionProperties PageSetup(MainDocumentPart main)
    {
        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = PageNumberFooter();
        footer.Footer.Save();

        return new SectionProperties(
            new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) },
            new PageSize { Width = 11906U, Height = 16838U },
            // 30mm on the binding edge, 20mm elsewhere.
            new PageMargin
            {
                Top = 1134,
                Right = 1134U,
                Bottom = 1134,
                Left = 1701U,
                Header = 709U,
                Footer = 709U,
                Gutter = 0U
            });
    }

    /// <summary>
    /// "Trang N/M", centred. Fields rather than literals: a document that has been printed and
    /// re-collated is checked against these numbers.
    /// </summary>
    private static Footer PageNumberFooter()
    {
        var paragraph = Shell(SmallSize, JustificationValues.Center, spaceAfter: 0);
        paragraph.AppendChild(TextRun("Trang ", SmallSize, italic: true));
        paragraph.AppendChild(Field("PAGE", SmallSize));
        paragraph.AppendChild(TextRun("/", SmallSize, italic: true));
        paragraph.AppendChild(Field("NUMPAGES", SmallSize));

        return new Footer(paragraph);
    }

    private static SimpleField Field(string instruction, int size) =>
        new(new Run(RunStyle(size, italic: true), new Text("1"))) { Instruction = instruction };

    // ------------------------------------------------------------------ blocks

    private static void WriteHeading(Body body, MeetingMinutesDto minutes, MeetingMinutesContent content)
    {
        body.AppendChild(Line(
            $"Số: {minutes.MinutesNo}", size: SmallSize, alignment: JustificationValues.Right));

        body.AppendChild(Line(
            "BIÊN BẢN CUỘC HỌP",
            size: TitleSize,
            bold: true,
            alignment: JustificationValues.Center,
            spaceBefore: 240,
            spaceAfter: 60,
            keepNext: true));

        if (!string.IsNullOrWhiteSpace(content.MeetingTitle))
        {
            body.AppendChild(Line(
                content.MeetingTitle!,
                size: 24,
                italic: true,
                alignment: JustificationValues.Center,
                spaceAfter: 0,
                keepNext: true));
        }

        // The status is on the face of the document, not only in a database column. A draft that
        // prints looking exactly like an approved record is how an unapproved one gets circulated.
        body.AppendChild(Line(
            StatusLine(minutes),
            size: 18,
            italic: true,
            alignment: JustificationValues.Center,
            spaceAfter: 240));
    }

    private static string StatusLine(MeetingMinutesDto minutes) => minutes.Status switch
    {
        "APPROVED" => minutes.Version > 1
            ? $"Đã thông qua — bản sửa đổi lần {minutes.Version - 1}"
            : "Đã thông qua",
        "IN_REVIEW" => "Thư ký đã ký — chờ chủ trì thông qua",
        _ => "BẢN NHÁP — chưa ký, chưa thông qua"
    };

    private static void WriteFacts(Body body, MeetingMinutesContent content)
    {
        body.AppendChild(Labelled("Thời gian khai mạc", Moment(content.OpenedAt)));
        body.AppendChild(Labelled("Thời gian bế mạc", Moment(content.ClosedAt)));
        if (content.ScheduledAt.HasValue)
        {
            body.AppendChild(Labelled("Theo lịch", Moment(content.ScheduledAt)));
        }
        body.AppendChild(Labelled("Địa điểm", content.Location ?? Blank));
    }

    /// <summary>
    /// Where the words came from, and in how many languages, said on the face of the document.
    ///
    /// A reader holding a signed page must be able to answer two questions without asking anybody:
    /// what am I reading, and what is it a record OF. So the source recording is named — with its
    /// version, when the record has one, because a transcript that was re-run is a different
    /// source — and every language present is counted and labelled. A translation printed beside
    /// an original without being called a translation is the way a machine rendering ends up
    /// quoted as somebody's words.
    ///
    /// Printed only from what the record holds: a minutes with no drafting engine was not built
    /// from a transcript by this system, and claiming it was would be an invented provenance.
    /// </summary>
    private static void WriteProvenance(Body body, MeetingMinutesDto minutes, MeetingMinutesContent content)
    {
        var languages = content.Translations?.Keys.OrderBy(code => code, StringComparer.Ordinal).ToList()
            ?? new List<string>();

        if (!string.IsNullOrWhiteSpace(content.PrimaryLanguage) || languages.Count > 0)
        {
            var original = string.IsNullOrWhiteSpace(content.PrimaryLanguage)
                ? "không xác định"
                : LanguageName(content.PrimaryLanguage!);

            var line = languages.Count == 0
                ? $"{original} (ngôn ngữ gốc của cuộc họp)"
                : $"{original} (ngôn ngữ gốc của cuộc họp); kèm {languages.Count} bản dịch — "
                  + string.Join(", ", languages.Select(LanguageName))
                  + ". Bản dịch do máy dịch từ lời gốc, không phải nguyên văn người dự họp nói.";

            body.AppendChild(Labelled("Ngôn ngữ", line));
        }

        if (!string.IsNullOrWhiteSpace(minutes.DraftedByEngine))
        {
            var version = minutes.BasedOnTranscriptVersion.HasValue
                ? $" phiên bản {minutes.BasedOnTranscriptVersion.Value}"
                : string.Empty;
            body.AppendChild(Labelled(
                "Cơ sở lập biên bản",
                $"bản ghi lời (transcript){version} của chính cuộc họp này do WarpTalk ghi nhận; "
                + "các mốc thời gian [mm:ss] trong biên bản trỏ về bản ghi đó."));
        }
    }

    /// <summary>
    /// A language code in words. Unknown codes print as the code itself rather than as a guess —
    /// "vi-VN" on the page is honest, an invented language name is not.
    /// </summary>
    private static string LanguageName(string code) => code.ToLowerInvariant() switch
    {
        "vi" => "tiếng Việt",
        "en" => "tiếng Anh",
        "ja" => "tiếng Nhật",
        "ko" => "tiếng Hàn",
        "zh" => "tiếng Trung",
        "fr" => "tiếng Pháp",
        "de" => "tiếng Đức",
        "es" => "tiếng Tây Ban Nha",
        "ru" => "tiếng Nga",
        "th" => "tiếng Thái",
        "id" => "tiếng Indonesia",
        _ => code
    };

    private static void WriteAttendance(Body body, MeetingMinutesContent content, RomanCounter numbering)
    {
        var attendance = content.Attendance;

        body.AppendChild(SectionHeading(numbering, "THÀNH PHẦN THAM DỰ"));

        var chair = attendance.Present.FirstOrDefault(
            person => string.Equals(person.Role, "HOST", StringComparison.OrdinalIgnoreCase));
        body.AppendChild(Labelled("Chủ trì", chair?.Name ?? Blank));

        if (attendance.Present.Count == 0)
        {
            body.AppendChild(Line("Không ghi nhận người tham dự.", left: Indent));
        }
        else
        {
            foreach (var person in attendance.Present)
            {
                var notes = new List<string>();
                if (person.IsExternal) notes.Add("khách ngoài đơn vị");
                if (!string.IsNullOrWhiteSpace(person.SpeakLanguage)) notes.Add($"phát biểu: {person.SpeakLanguage}");
                if (person.JoinedAt.HasValue) notes.Add($"vào lúc {Moment(person.JoinedAt)}");

                var suffix = notes.Count > 0 ? $" ({string.Join("; ", notes)})" : string.Empty;
                body.AppendChild(Bullet($"{person.Name}{suffix}", left: Indent));
            }
        }

        if (attendance.Absent.Count > 0)
        {
            body.AppendChild(Line("Vắng mặt:", bold: true, spaceBefore: 80));
            foreach (var person in attendance.Absent)
            {
                var reason = string.IsNullOrWhiteSpace(person.Reason) ? Blank : person.Reason!;
                body.AppendChild(Bullet($"{person.Name} (lý do: {reason})", left: Indent));
            }
        }

        // Printed with the rule beside it. Quorum is the line that gets disputed later, and
        // "đủ điều kiện" on its own does not say what bar was applied.
        if (attendance.QuorumMet.HasValue)
        {
            var verdict = attendance.QuorumMet.Value
                ? "đủ điều kiện tiến hành"
                : "CHƯA đủ điều kiện tiến hành";
            var rule = string.IsNullOrWhiteSpace(attendance.QuorumRule)
                ? string.Empty
                : $" — {attendance.QuorumRule}";
            body.AppendChild(Line(
                $"Có mặt {attendance.PresentCount}/{attendance.InvitedCount} người được mời: {verdict}{rule}.",
                italic: true,
                spaceBefore: 80));
        }
    }

    private static void WriteAgenda(Body body, MeetingMinutesContent content, RomanCounter numbering)
    {
        body.AppendChild(SectionHeading(numbering, "CHƯƠNG TRÌNH HỌP"));
        WriteRichText(body, content.Agenda);

        // The blank alone reads as a bug in the exporter. Saying why it is blank turns it back
        // into what it is: a room booked without a programme, and a line for a person to fill.
        if (string.IsNullOrWhiteSpace(content.Agenda))
        {
            body.AppendChild(Line(
                "(Cuộc họp không đăng ký chương trình từ trước; phần này do thư ký bổ sung.)",
                size: 18,
                italic: true,
                left: Indent));
        }
    }

    private static void WriteSections(Body body, MeetingMinutesContent content, RomanCounter numbering)
    {
        body.AppendChild(SectionHeading(numbering, "NỘI DUNG CUỘC HỌP"));

        if (content.Sections.Count == 0)
        {
            body.AppendChild(Line(Blank, left: Indent));
        }

        var languages = content.Translations?.Keys.OrderBy(code => code, StringComparer.Ordinal).ToList()
            ?? new List<string>();

        // Arabic numbering under the Roman heading, the way a biên bản numbers its business.
        var order = 0;

        foreach (var section in content.Sections)
        {
            order++;
            body.AppendChild(Line(
                $"{order}. {SectionTitle(section.Key)}",
                bold: true,
                left: Indent,
                spaceBefore: 160,
                spaceAfter: 60,
                keepNext: true));

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

            // One language pairs line-by-line, the rest print as blocks. Interleaving several
            // languages under every line turns a decision into a wall; the first that pairs is the
            // one a bilingual room actually has.
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

            // Every language that did not pair — and every language at all when none did — prints
            // whole underneath. A block asserts nothing about any individual line, which is the
            // honest thing to say when the citations do not line up.
            foreach (var language in languages)
            {
                if (paired != null && language == paired.Language) continue;

                var translated = MinutesBilingualPairing.CounterpartOf(
                    section, content.Translations![language]);
                if (translated?.Items == null || translated.Items.Count == 0) continue;

                body.AppendChild(Line($"[{language}]", size: 18, italic: true, left: Indent));
                foreach (var item in translated.Items)
                {
                    body.AppendChild(TranslatedLine(language, item.Text, withPrefix: false));
                }
            }
        }
    }

    /// <summary>An original line: the words, who owns it, and the moment it came from.</summary>
    private static Paragraph ItemLine(MinutesItem item)
    {
        var paragraph = Shell(BodySize, left: Indent * 2, hanging: Indent);
        paragraph.AppendChild(TextRun("- ", BodySize));
        AppendInline(paragraph, item.Text ?? string.Empty, BodySize);

        if (!string.IsNullOrWhiteSpace(item.Owner))
        {
            paragraph.AppendChild(TextRun($" — {item.Owner}", BodySize));
        }

        // The citation is carried onto the printed page, set smaller so it reads as apparatus
        // rather than as part of the sentence. It is what lets a reader of the paper copy go back
        // to the recording and check a line somebody signed for.
        if (item.AtMs.HasValue)
        {
            paragraph.AppendChild(TextRun($" [{Offset(item.AtMs.Value)}]", SmallSize, italic: true));
        }

        return paragraph;
    }

    /// <summary>
    /// A translated line, visibly subordinate to the original.
    ///
    /// Indented further and set in smaller italic on purpose: in a bilingual record it must be
    /// unmistakable which text is what was said and which is a rendering of it. A translation
    /// typeset identically to the original is a translation somebody will later quote as the
    /// original.
    /// </summary>
    private static Paragraph TranslatedLine(string language, string text, bool withPrefix = true)
    {
        var paragraph = Shell(SmallSize, left: Indent * 3, hanging: withPrefix ? Indent : 0);
        if (withPrefix)
        {
            paragraph.AppendChild(TextRun($"[{language}] ", SmallSize, italic: true));
        }
        AppendInline(paragraph, text, SmallSize, italic: true);
        return paragraph;
    }

    private static void WriteVotes(Body body, MeetingMinutesContent content, RomanCounter numbering)
    {
        // Omitted entirely when nobody voted, rather than printed as an empty heading. A blank
        // "BIỂU QUYẾT" invites somebody to read a vote that never happened into the gap.
        if (content.Votes.Count == 0) return;

        body.AppendChild(SectionHeading(numbering, "BIỂU QUYẾT"));
        foreach (var vote in content.Votes)
        {
            body.AppendChild(Bullet(
                $"{TopicOrBlank(vote)}: tán thành {vote.ForCount}, không tán thành {vote.AgainstCount}, "
                + $"không ý kiến {vote.AbstainCount}",
                left: Indent));
        }
    }

    private static void WriteClosing(
        Body body, MeetingMinutesDto minutes, MeetingMinutesContent content, RomanCounter numbering)
    {
        body.AppendChild(SectionHeading(numbering, "KẾT LUẬN"));

        var closed = content.ClosedAt.HasValue ? Moment(content.ClosedAt) : Blank;
        // Only an approved record may say it was read back and adopted — that sentence describes
        // something that happens in the room, and printing it on a draft asserts a meeting event
        // nobody has evidence of.
        var adoption = minutes.Status == "APPROVED"
            ? "Biên bản đã được đọc lại cho những người dự họp cùng nghe và thống nhất thông qua."
            : "Biên bản chưa được thông qua tại thời điểm xuất bản văn bản này.";
        body.AppendChild(Line($"Cuộc họp kết thúc vào lúc {closed}. {adoption}", left: Indent));

        if (!string.IsNullOrWhiteSpace(content.Notes))
        {
            body.AppendChild(Line("Ghi chú của thư ký:", bold: true, spaceBefore: 120));
            WriteRichText(body, content.Notes);
        }
    }

    private static void WriteSignatures(Body body, MeetingMinutesDto minutes)
    {
        // Stated before the names, in small type. A reader must not have to infer that a program
        // wrote the first version of what they are about to read.
        var drafted = minutes.DraftedAt.HasValue ? $" lúc {Moment(minutes.DraftedAt)}" : string.Empty;
        body.AppendChild(Line(
            $"Bản nháp do {minutes.DraftedByEngine ?? "hệ thống"} lập{drafted}; "
            + "thư ký rà soát và chịu trách nhiệm về nội dung.",
            size: 18,
            italic: true,
            spaceBefore: 240));

        // The number a reader uses to judge whether anybody actually read the draft.
        if (minutes.SecretarySignedAt.HasValue)
        {
            var edits = minutes.EditCountVsDraft > 0
                ? $"Thư ký đã sửa {minutes.EditCountVsDraft} điểm so với bản nháp."
                : "Thư ký giữ nguyên bản nháp.";
            body.AppendChild(Line(edits, size: 18, italic: true));
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
                // The two blocks stay on one page together: a signature block split across a page
                // break is the first thing a reader distrusts.
                new TableRowProperties(new CantSplit()),
                SignatureCell("THƯ KÝ", minutes.SecretaryName, minutes.SecretarySignedAt),
                SignatureCell("CHỦ TRÌ", minutes.ChairName, minutes.ChairApprovedAt)));

        body.AppendChild(table);
    }

    private static TableCell SignatureCell(string role, string? name, DateTime? signedAt)
    {
        var cell = new TableCell(
            new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Pct, Width = "2500" }));

        cell.AppendChild(Line(role, bold: true, alignment: JustificationValues.Center, spaceAfter: 0));
        cell.AppendChild(Line(
            signedAt.HasValue ? $"(đã ký {Moment(signedAt)})" : "(chưa ký)",
            size: 18,
            italic: true,
            alignment: JustificationValues.Center));
        // The empty lines a wet signature needs on a printed copy.
        cell.AppendChild(Spacer());
        cell.AppendChild(Spacer());
        cell.AppendChild(Line(
            string.IsNullOrWhiteSpace(name) ? Blank : name!,
            bold: true,
            alignment: JustificationValues.Center));

        return cell;
    }

    // ------------------------------------------------------------------ narrative

    /// <summary>
    /// Prose the model wrote, set as Word blocks instead of printed as source.
    ///
    /// The overview arrives as Markdown — "## Meeting Summary", "- ", "**80%**" — and a run of raw
    /// Markdown is not a paragraph, it is a wall of punctuation on a page somebody signs.
    /// <see cref="MinutesMarkdown"/> reads it; this method decides how the Vietnamese form sets
    /// it. Empty text prints the blank a paper form would have, never nothing.
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
                        block.Text,
                        bold: true,
                        left: left,
                        spaceBefore: 120,
                        spaceAfter: 40,
                        keepNext: true));
                    break;

                case MinutesMarkdown.BlockKind.Item:
                    body.AppendChild(RichLine(
                        block.Text,
                        left: left + Indent + (block.Depth * Indent),
                        hanging: Indent,
                        // The model's own numbering is kept: a list renumbered by a renderer stops
                        // matching the sentence above it that referred to item 3.
                        prefix: block.Marker != null ? $"{block.Marker} " : "- "));
                    break;

                default:
                    body.AppendChild(RichLine(block.Text, left: left));
                    break;
            }
        }
    }

    private static void AppendInline(Paragraph paragraph, string text, int size, bool bold = false, bool italic = false)
    {
        foreach (var span in MinutesMarkdown.InlineSpans(text))
        {
            paragraph.AppendChild(TextRun(span.Text, size, bold || span.Bold, italic || span.Italic));
        }
    }

    // ------------------------------------------------------------------ primitives

    /// <summary>Roman numerals for the top-level headings, handed out in the order they print.</summary>
    private sealed class RomanCounter
    {
        private static readonly string[] Numerals = { "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };
        private int _used;

        public string Next()
        {
            var index = _used++;
            return index < Numerals.Length
                ? Numerals[index]
                : (index + 1).ToString(CultureInfo.InvariantCulture);
        }
    }

    private static Paragraph SectionHeading(RomanCounter numbering, string title) => Line(
        $"{numbering.Next()}. {title}",
        bold: true,
        alignment: JustificationValues.Left,
        spaceBefore: 240,
        spaceAfter: 100,
        keepNext: true);

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
        if (spaceBefore.HasValue) spacing.Before = spaceBefore.Value.ToString(CultureInfo.InvariantCulture);
        if (spaceAfter.HasValue) spacing.After = spaceAfter.Value.ToString(CultureInfo.InvariantCulture);
        properties.AppendChild(spacing);

        if (left > 0 || hanging > 0)
        {
            var indentation = new Indentation();
            if (left > 0) indentation.Left = left.ToString(CultureInfo.InvariantCulture);
            // Hanging, not first-line: a wrapped bullet must align under its own text, not under
            // the dash.
            if (hanging > 0) indentation.Hanging = hanging.ToString(CultureInfo.InvariantCulture);
            properties.AppendChild(indentation);
        }

        properties.AppendChild(new Justification { Val = alignment ?? JustificationValues.Both });

        return new Paragraph(properties);
    }

    private static RunProperties RunStyle(int size, bool bold = false, bool italic = false)
    {
        // Same schema-order rule as paragraphs: rFonts → b → i → sz.
        var properties = new RunProperties(
            new RunFonts { Ascii = BodyFont, HighAnsi = BodyFont, ComplexScript = BodyFont });
        if (bold) properties.AppendChild(new Bold());
        if (italic) properties.AppendChild(new Italic());
        properties.AppendChild(new FontSize { Val = size.ToString(CultureInfo.InvariantCulture) });
        properties.AppendChild(new FontSizeComplexScript { Val = size.ToString(CultureInfo.InvariantCulture) });
        return properties;
    }

    private static Run TextRun(string text, int size, bool bold = false, bool italic = false)
    {
        var run = new Run(RunStyle(size, bold, italic));

        // Split on newlines rather than emitting them raw: a bare \n in a Word run is not a line
        // break, it is nothing, so a multi-line agenda would arrive as one run-on sentence.
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

    /// <summary>A dash-led line with the hanging indent that keeps its wrap under its own text.</summary>
    private static Paragraph Bullet(string text, int left = 0) =>
        RichLine(text, left: left + Indent, hanging: Indent, prefix: "- ");

    private static Paragraph Spacer() => new(
        new ParagraphProperties(new SpacingBetweenLines { Before = "0", After = "0" }),
        new Run(RunStyle(SmallSize), new Text(string.Empty)));

    private static Paragraph Labelled(string label, string value)
    {
        var paragraph = Shell();
        paragraph.AppendChild(TextRun($"{label}: ", BodySize, bold: true));
        AppendInline(paragraph, value, BodySize);
        return paragraph;
    }

    /// <summary>A timestamp as it is written on a Vietnamese form, or the blank when absent.</summary>
    private static string Moment(DateTime? value)
    {
        if (!value.HasValue) return Blank;
        var local = DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToUniversalTime();
        return local.ToString("HH'h'mm' ngày 'dd/MM/yyyy", Vietnamese) + " (UTC)";
    }

    /// <summary>A vote with no topic still gets a line; the blank shows something is missing.</summary>
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
        "carriedOver" => "Công việc tồn từ kỳ trước",
        "summary" => "Tóm tắt",
        "decisions" => "Các quyết định",
        "actionItems" => "Công việc được giao",
        "openQuestions" => "Vấn đề còn bỏ ngỏ",
        "progress" => "Tiến độ",
        "plans" => "Kế hoạch",
        "blockers" => "Vướng mắc",
        "background" => "Bối cảnh",
        "strengths" => "Điểm mạnh",
        "concerns" => "Điểm lo ngại",
        "shown" => "Nội dung đã trình bày",
        "reactions" => "Phản hồi",
        "objections" => "Ý kiến phản đối",
        "problems" => "Vấn đề nêu ra",
        "options" => "Các phương án",
        _ => key
    };
}
