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

namespace WarpTalk.TranslationRoomService.Infrastructure.Documents;

/// <summary>
/// A biên bản as a Word document, in the order Vietnamese practice reads it.
///
/// THE ORDER IS THE SPEC
///     Đơn vị → số biên bản → tên biên bản → thời gian và địa điểm → thành phần tham dự → vắng mặt
///     → chương trình → nội dung → biểu quyết → kết luận về thời gian bế mạc → chữ ký. A reader
///     who has signed a hundred of these finds each part where they expect it; a document that
///     rearranges them reads as something else that happens to contain the same facts.
///
/// TWO SIGNATURE BLOCKS SIDE BY SIDE
///     Thư ký on the left, Chủ trì on the right, which is where a printed biên bản puts them.
///     Above them, on its own line and in smaller type, the program that produced the draft. That
///     line is not a disclaimer — it is a fact about the document, and hiding it would let a
///     reader assume a person wrote every word.
///
/// NO DATES THIS CLASS INVENTS
///     Every timestamp printed comes from the record. A missing one prints as "………" — the blank a
///     paper form would have — rather than as today's date, which is the single most dangerous
///     thing a document generator can do to a record with legal weight.
/// </summary>
public class MeetingMinutesDocxWriter : IMeetingMinutesDocumentWriter
{
    /// <summary>The blank a paper form leaves for something nobody has filled in.</summary>
    private const string Blank = "…………………";

    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");

    public byte[] WriteDocx(MeetingMinutesDto minutes, MeetingMinutesContent content)
    {
        using var stream = new MemoryStream();

        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document();
            var body = main.Document.AppendChild(new Body());

            WriteHeading(body, minutes, content);
            WriteFacts(body, content);
            WriteAttendance(body, content);
            WriteAgenda(body, content);
            WriteSections(body, content);
            WriteVotes(body, content);
            WriteClosing(body, content);
            WriteSignatures(body, minutes);

            main.Document.Save();
        }

        return stream.ToArray();
    }

    // ------------------------------------------------------------------ blocks

    private static void WriteHeading(Body body, MeetingMinutesDto minutes, MeetingMinutesContent content)
    {
        body.AppendChild(Line($"Số: {minutes.MinutesNo}", size: 20, alignment: JustificationValues.Right));

        body.AppendChild(Line("BIÊN BẢN CUỘC HỌP", size: 32, bold: true, alignment: JustificationValues.Center));

        if (!string.IsNullOrWhiteSpace(content.MeetingTitle))
        {
            body.AppendChild(Line(content.MeetingTitle!, size: 24, italic: true, alignment: JustificationValues.Center));
        }

        // The status is on the face of the document, not only in a database column. A draft that
        // prints looking exactly like an approved record is how an unapproved one gets circulated.
        body.AppendChild(Line(StatusLine(minutes), size: 18, italic: true, alignment: JustificationValues.Center));
        body.AppendChild(Spacer());
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
        body.AppendChild(Spacer());
    }

    private static void WriteAttendance(Body body, MeetingMinutesContent content)
    {
        var attendance = content.Attendance;

        body.AppendChild(Line("I. THÀNH PHẦN THAM DỰ", size: 24, bold: true));

        var chair = attendance.Present.FirstOrDefault(
            person => string.Equals(person.Role, "HOST", StringComparison.OrdinalIgnoreCase));
        body.AppendChild(Labelled("Chủ trì", chair?.Name ?? Blank));

        if (attendance.Present.Count == 0)
        {
            body.AppendChild(Line("Không ghi nhận người tham dự.", indent: true));
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
                body.AppendChild(Line($"- {person.Name}{suffix}", indent: true));
            }
        }

        if (attendance.Absent.Count > 0)
        {
            body.AppendChild(Line("Vắng mặt:", bold: true));
            foreach (var person in attendance.Absent)
            {
                var reason = string.IsNullOrWhiteSpace(person.Reason) ? Blank : person.Reason!;
                body.AppendChild(Line($"- {person.Name} (lý do: {reason})", indent: true));
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
                italic: true));
        }

        body.AppendChild(Spacer());
    }

    private static void WriteAgenda(Body body, MeetingMinutesContent content)
    {
        body.AppendChild(Line("II. CHƯƠNG TRÌNH HỌP", size: 24, bold: true));
        body.AppendChild(Line(
            string.IsNullOrWhiteSpace(content.Agenda) ? Blank : content.Agenda!,
            indent: true));
        body.AppendChild(Spacer());
    }

    private static void WriteSections(Body body, MeetingMinutesContent content)
    {
        body.AppendChild(Line("III. NỘI DUNG CUỘC HỌP", size: 24, bold: true));

        if (content.Sections.Count == 0)
        {
            body.AppendChild(Line(Blank, indent: true));
        }

        var languages = content.Translations?.Keys.OrderBy(code => code, StringComparer.Ordinal).ToList()
            ?? new List<string>();

        foreach (var section in content.Sections)
        {
            body.AppendChild(Line(SectionTitle(section.Key), size: 22, bold: true, indent: true));

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

            // Every language that did not pair — and every language at all when none did — prints
            // whole underneath. A block asserts nothing about any individual line, which is the
            // honest thing to say when the citations do not line up.
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

    /// <summary>An original line: the words, who owns it, and the moment it came from.</summary>
    private static string OriginalLine(MinutesItem item)
    {
        var owner = string.IsNullOrWhiteSpace(item.Owner) ? string.Empty : $" — {item.Owner}";
        // The citation is carried onto the printed page. It is what lets a reader of the paper
        // copy go back to the recording and check a line somebody signed for.
        var citation = item.AtMs.HasValue ? $" [{Offset(item.AtMs.Value)}]" : string.Empty;
        return $"- {item.Text}{owner}{citation}";
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
        var prefix = withPrefix ? $"[{language}] " : "  ";
        var paragraph = Line($"{prefix}{text}", size: 20, italic: true);
        paragraph.ParagraphProperties?.AppendChild(new Indentation { Left = "720" });
        return paragraph;
    }

    private static void WriteVotes(Body body, MeetingMinutesContent content)
    {
        // Omitted entirely when nobody voted, rather than printed as an empty heading. A blank
        // "IV. BIỂU QUYẾT" invites somebody to read a vote that never happened into the gap.
        if (content.Votes.Count == 0) return;

        body.AppendChild(Line("IV. BIỂU QUYẾT", size: 24, bold: true));
        foreach (var vote in content.Votes)
        {
            body.AppendChild(Line(
                $"- {TopicOrBlank(vote)}: tán thành {vote.ForCount}, không tán thành {vote.AgainstCount}, "
                + $"không ý kiến {vote.AbstainCount}",
                indent: true));
        }
        body.AppendChild(Spacer());
    }

    private static void WriteClosing(Body body, MeetingMinutesContent content)
    {
        body.AppendChild(Line("KẾT LUẬN", size: 24, bold: true));

        var closed = content.ClosedAt.HasValue ? Moment(content.ClosedAt) : Blank;
        body.AppendChild(Line(
            $"Cuộc họp kết thúc vào lúc {closed}. Biên bản đã được đọc lại cho những người dự họp "
            + "cùng nghe và thống nhất thông qua.",
            indent: true));

        if (!string.IsNullOrWhiteSpace(content.Notes))
        {
            body.AppendChild(Line("Ghi chú của thư ký:", bold: true));
            body.AppendChild(Line(content.Notes!, indent: true));
        }

        body.AppendChild(Spacer());
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
            italic: true));

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
            new TableProperties(new TableBorders(
                new TopBorder { Val = BorderValues.None },
                new BottomBorder { Val = BorderValues.None },
                new LeftBorder { Val = BorderValues.None },
                new RightBorder { Val = BorderValues.None },
                new InsideHorizontalBorder { Val = BorderValues.None },
                new InsideVerticalBorder { Val = BorderValues.None })),
            new TableRow(
                SignatureCell("THƯ KÝ", minutes.SecretaryName, minutes.SecretarySignedAt),
                SignatureCell("CHỦ TRÌ", minutes.ChairName, minutes.ChairApprovedAt)));

        body.AppendChild(table);
    }

    private static TableCell SignatureCell(string role, string? name, DateTime? signedAt)
    {
        var cell = new TableCell(
            new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Pct, Width = "2500" }));

        cell.AppendChild(Line(role, size: 22, bold: true, alignment: JustificationValues.Center));
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
            size: 22,
            bold: true,
            alignment: JustificationValues.Center));

        return cell;
    }

    // ------------------------------------------------------------------ primitives

    private static Paragraph Line(
        string text,
        int size = 22,
        bool bold = false,
        bool italic = false,
        bool indent = false,
        JustificationValues? alignment = null)
    {
        var runProperties = new RunProperties(new FontSize { Val = size.ToString(CultureInfo.InvariantCulture) });
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

    private static Paragraph Labelled(string label, string value) => Line($"{label}: {value}");

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
