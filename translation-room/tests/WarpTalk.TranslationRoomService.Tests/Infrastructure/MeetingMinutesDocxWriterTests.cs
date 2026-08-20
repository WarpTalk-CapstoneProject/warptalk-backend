using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Infrastructure.Documents;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure;

/// <summary>
/// What the exported biên bản must and must not say.
///
/// The dangerous failures of a document generator are not crashes — they are documents that look
/// finished and are not. A draft that prints like an approved record, or a blank date filled in
/// with today's, produces a file somebody circulates and relies on. Those two are pinned here
/// first; the rest is the standard form being present in the order a reader expects it.
/// </summary>
public class MeetingMinutesDocxWriterTests
{
    // Deliberately not today. AMissingTimestampPrintsABlankAndNeverTodaysDate asserts that
    // today's date appears nowhere, and a fixture dated today would make that assertion catch
    // a legitimate timestamp — passing or failing depending on the day it is run.
    private static readonly DateTime Opened = new(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Closed = new(2026, 3, 10, 10, 5, 0, DateTimeKind.Utc);

    private static MeetingMinutesDto Minutes(
        string status = "APPROVED",
        int version = 1,
        string? secretary = "Ngô Xuân Hạnh Nhi",
        string? chair = "Huỳnh Thái Tú",
        int edits = 3) => new(
        Id: Guid.NewGuid(),
        TranslationRoomId: Guid.NewGuid(),
        MinutesNo: "BB-2026-0007",
        Status: status,
        Version: version,
        IsCurrent: true,
        PreviousMinutesId: null,
        BasedOnTranscriptVersion: null,
        DraftedByEngine: "warptalk-ai/meeting-summary",
        DraftedAt: Closed,
        SecretaryParticipantId: Guid.NewGuid(),
        SecretaryName: secretary,
        SecretarySignedAt: status == "DRAFT" ? null : Closed,
        ChairParticipantId: Guid.NewGuid(),
        ChairName: chair,
        ChairApprovedAt: status == "APPROVED" ? Closed : null,
        EditCountVsDraft: edits,
        Content: "{}",
        CreatedAt: Closed,
        UpdatedAt: Closed);

    private static MeetingMinutesContent Content() => new()
    {
        MeetingTitle = "Sprint review",
        Location = "Trực tuyến qua WarpTalk",
        OpenedAt = Opened,
        ClosedAt = Closed,
        Attendance = new MinutesAttendance
        {
            Present = new List<MinutesAttendee>
            {
                new() { ParticipantId = Guid.NewGuid(), Name = "Huỳnh Thái Tú", Role = "HOST", JoinedAt = Opened },
                new() { ParticipantId = Guid.NewGuid(), Name = "Khách Ngoài", IsExternal = true, JoinedAt = Opened }
            },
            Absent = new List<MinutesAbsentee>
            {
                new() { ParticipantId = Guid.NewGuid(), Name = "Trần Mạnh Tuấn" }
            },
            InvitedCount = 3,
            PresentCount = 2,
            QuorumRule = "Quá bán số người được mời",
            QuorumMet = true
        },
        Sections = new List<MinutesSection>
        {
            new() { Key = "summary", Kind = "paragraph", Text = "Đã rà soát sprint." },
            new()
            {
                Key = "decisions",
                Kind = "items",
                Items = new List<MinutesItem>
                {
                    new() { Text = "Phát hành thứ Sáu", AtMs = 125000 }
                }
            }
        },
        Votes = new List<MinutesVote>()
    };

    private static string TextOf(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var document = WordprocessingDocument.Open(stream, false);
        return document.MainDocumentPart?.Document?.InnerText ?? string.Empty;
    }

    [Fact]
    public void TheFileIsAValidWordDocument()
    {
        var bytes = new MeetingMinutesDocxWriter().WriteDocx(Minutes(), Content());

        bytes.Should().NotBeEmpty();
        // Opening it is the assertion: an invalid package throws here rather than reaching a user
        // as a file Word refuses.
        TextOf(bytes).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ADraftSaysSoOnItsFace()
    {
        // A draft that prints identically to an approved record is how an unapproved one gets
        // circulated and relied on.
        var text = TextOf(new MeetingMinutesDocxWriter().WriteDocx(Minutes(status: "DRAFT"), Content()));

        text.Should().Contain("BẢN NHÁP");
        text.Should().Contain("chưa ký");
    }

    [Fact]
    public void AnApprovedDocumentSaysItWasApproved()
    {
        var text = TextOf(new MeetingMinutesDocxWriter().WriteDocx(Minutes(), Content()));

        text.Should().Contain("Đã thông qua");
        text.Should().NotContain("BẢN NHÁP");
    }

    [Fact]
    public void AMissingTimestampPrintsABlankAndNeverTodaysDate()
    {
        // The single most dangerous thing a document generator can do to a record with legal
        // weight is fill an empty date with the date it happened to run.
        var content = Content();
        content.ClosedAt = null;
        content.OpenedAt = null;

        var text = TextOf(new MeetingMinutesDocxWriter().WriteDocx(Minutes(), content));

        text.Should().Contain("…………………");
        text.Should().NotContain(DateTime.UtcNow.ToString("dd/MM/yyyy"));
    }

    [Fact]
    public void TheStandardSectionsAppearInTheOrderAReaderExpects()
    {
        var text = TextOf(new MeetingMinutesDocxWriter().WriteDocx(Minutes(), Content()));

        var order = new[]
        {
            "BB-2026-0007",
            "BIÊN BẢN CUỘC HỌP",
            "I. THÀNH PHẦN THAM DỰ",
            "II. CHƯƠNG TRÌNH HỌP",
            "III. NỘI DUNG CUỘC HỌP",
            "KẾT LUẬN",
            "THƯ KÝ"
        };

        var positions = order.Select(marker => text.IndexOf(marker, StringComparison.Ordinal)).ToList();
        positions.Should().NotContain(-1);
        positions.Should().BeInAscendingOrder();
    }

    [Fact]
    public void AttendanceAbsenceAndQuorumAllReachThePage()
    {
        var text = TextOf(new MeetingMinutesDocxWriter().WriteDocx(Minutes(), Content()));

        text.Should().Contain("Huỳnh Thái Tú");
        text.Should().Contain("Khách Ngoài");
        text.Should().Contain("khách ngoài đơn vị");
        text.Should().Contain("Vắng mặt");
        text.Should().Contain("Trần Mạnh Tuấn");
        // The rule beside the verdict, because quorum is what gets disputed later.
        text.Should().Contain("2/3");
        text.Should().Contain("Quá bán số người được mời");
    }

    [Fact]
    public void CitationsSurviveOntoThePrintedPage()
    {
        // The timestamp is what lets a reader of the paper copy check a signed line against the
        // recording. Dropping it in export would leave a document that looks verifiable and is not.
        var text = TextOf(new MeetingMinutesDocxWriter().WriteDocx(Minutes(), Content()));

        text.Should().Contain("Phát hành thứ Sáu");
        text.Should().Contain("02:05");
    }

    [Fact]
    public void BothTheDraftingProgramAndTheAnswerablePersonArePrinted()
    {
        var text = TextOf(new MeetingMinutesDocxWriter().WriteDocx(Minutes(), Content()));

        text.Should().Contain("warptalk-ai/meeting-summary");
        text.Should().Contain("Ngô Xuân Hạnh Nhi");
        // The reader's evidence that a person actually read the draft.
        text.Should().Contain("đã sửa 3 điểm");
    }

    [Fact]
    public void AnEmptyVoteSectionIsOmittedRatherThanPrintedBlank()
    {
        // A blank "BIỂU QUYẾT" heading invites a reader to imagine a vote into the gap.
        var text = TextOf(new MeetingMinutesDocxWriter().WriteDocx(Minutes(), Content()));

        text.Should().NotContain("BIỂU QUYẾT");
    }

    [Fact]
    public void RecordedVotesArePrintedWithTheirCounts()
    {
        var content = Content();
        content.Votes.Add(new MinutesVote
        {
            Topic = "Phát hành thứ Sáu",
            ForCount = 4,
            AgainstCount = 1,
            AbstainCount = 2
        });

        var text = TextOf(new MeetingMinutesDocxWriter().WriteDocx(Minutes(), content));

        text.Should().Contain("BIỂU QUYẾT");
        text.Should().Contain("tán thành 4");
        text.Should().Contain("không tán thành 1");
        text.Should().Contain("không ý kiến 2");
    }

    [Fact]
    public void ARevisionSaysWhichRevisionItIs()
    {
        var text = TextOf(new MeetingMinutesDocxWriter().WriteDocx(Minutes(version: 3), Content()));

        text.Should().Contain("bản sửa đổi lần 2");
    }

    [Fact]
    public void AnUnsignedDocumentLeavesTheSignatureLineBlankRatherThanNamingNobody()
    {
        var text = TextOf(new MeetingMinutesDocxWriter().WriteDocx(
            Minutes(status: "DRAFT", secretary: null, chair: null), Content()));

        text.Should().Contain("THƯ KÝ");
        text.Should().Contain("CHỦ TRÌ");
        text.Should().Contain("…………………");
    }

    [Fact]
    public void AMultiLineAgendaKeepsItsLines()
    {
        // A bare \n inside a Word run is not a line break, it is nothing — an agenda written as a
        // numbered list would arrive as one run-on sentence.
        var content = Content();
        content.Agenda = "1. Rà soát sprint\n2. Kế hoạch phát hành";

        var text = TextOf(new MeetingMinutesDocxWriter().WriteDocx(Minutes(), content));

        text.Should().Contain("1. Rà soát sprint");
        text.Should().Contain("2. Kế hoạch phát hành");
    }
}
