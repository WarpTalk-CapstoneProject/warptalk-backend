using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Infrastructure.Documents;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure;

/// <summary>
/// Choosing the layout a biên bản is exported in.
///
/// The two templates are the same record on a different page. These pin the two things that must
/// stay true of that arrangement: which one you get when you do not ask, and that the choice
/// changes presentation only — never what the document says about the meeting.
/// </summary>
public class MinutesTemplateChoiceTests
{
    private static readonly DateTime Opened = new(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Closed = new(2026, 3, 10, 10, 5, 0, DateTimeKind.Utc);

    private static MeetingMinutesDto Minutes(string status = "APPROVED", int version = 1) => new(
        Id: Guid.NewGuid(),
        TranslationRoomId: Guid.NewGuid(),
        MinutesNo: "BB-2026-0007",
        MeetingTitle: "Sprint review",
        Status: status,
        Version: version,
        IsCurrent: true,
        PreviousMinutesId: null,
        BasedOnTranscriptVersion: null,
        DraftedByEngine: "warptalk-ai/meeting-summary",
        DraftedAt: Closed,
        SecretaryParticipantId: Guid.NewGuid(),
        SecretaryName: "Ngô Xuân Hạnh Nhi",
        SecretarySignedAt: status == "DRAFT" ? null : Closed,
        ChairParticipantId: Guid.NewGuid(),
        ChairName: "Huỳnh Thái Tú",
        ChairApprovedAt: status == "APPROVED" ? Closed : null,
        EditCountVsDraft: 3,
        Content: "{}",
        CreatedAt: Opened,
        UpdatedAt: Closed);

    private static MeetingMinutesContent Content() => new()
    {
        MeetingTitle = "Sprint review",
        Location = "Trực tuyến qua WarpTalk",
        OpenedAt = Opened,
        ClosedAt = Closed,
        Agenda = "Review the sprint",
        Attendance = new MinutesAttendance
        {
            Present = new List<MinutesAttendee>
            {
                new() { ParticipantId = Guid.NewGuid(), Name = "Trần Quang Huy", Role = "HOST" },
                new() { ParticipantId = Guid.NewGuid(), Name = "Lê Thị Mai" }
            },
            InvitedCount = 3,
            PresentCount = 2,
            QuorumRule = "Quá bán số người được mời",
            QuorumMet = true
        },
        Sections = new List<MinutesSection>
        {
            new()
            {
                Key = "decisions",
                Kind = "items",
                Items = new List<MinutesItem>
                {
                    new() { Text = "Ship the export on Friday", Owner = "Huy", AtMs = 754_000 }
                }
            }
        },
        Votes = new List<MinutesVote>()
    };

    private static string TextOf(byte[] docx)
    {
        using var stream = new MemoryStream(docx);
        using var document = WordprocessingDocument.Open(stream, false);
        return document.MainDocumentPart!.Document!.Body!.InnerText;
    }

    [Fact]
    public void Asking_for_nothing_gives_the_global_template()
    {
        // The default is the English form: a reader who does not read Vietnamese cannot check a
        // document set in Nghị định 30 form, and a wrong default in that direction is unreadable
        // rather than merely unfamiliar.
        var text = TextOf(new MinutesDocumentWriter().WriteDocx(
            Minutes(), Content(), MinutesTemplates.Default));

        text.Should().Contain("MINUTES OF MEETING");
        text.Should().NotContain("BIÊN BẢN CUỘC HỌP");
    }

    [Fact]
    public void The_default_constant_is_the_global_template()
    {
        MinutesTemplates.Default.Should().Be(MinutesTemplates.GlobalEn);
    }

    [Fact]
    public void Asking_for_the_vietnamese_template_gives_the_vietnamese_form()
    {
        var text = TextOf(new MinutesDocumentWriter().WriteDocx(
            Minutes(), Content(), MinutesTemplates.VnNd30));

        text.Should().Contain("BIÊN BẢN CUỘC HỌP");
        text.Should().Contain("I. THÀNH PHẦN THAM DỰ");
        text.Should().NotContain("MINUTES OF MEETING");
    }

    [Theory]
    [InlineData("GLOBAL-EN")]
    [InlineData("Vn-Nd30")]
    public void The_template_name_is_matched_case_insensitively(string requested)
    {
        // It travels in a query string and gets typed by hand. Case is not a choice the caller
        // was making.
        MinutesTemplates.IsKnown(requested).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nd30")]
    [InlineData("../../etc/passwd")]
    public void An_unrecognised_template_renders_the_default_rather_than_failing(string? requested)
    {
        // Refusing to hand somebody their own minutes because a query string carried a typo trades
        // a readable document for no document.
        MinutesTemplates.Normalise(requested).Should().Be(MinutesTemplates.GlobalEn);

        var text = TextOf(new MinutesDocumentWriter().WriteDocx(Minutes(), Content(), requested!));
        text.Should().Contain("MINUTES OF MEETING");
    }

    [Fact]
    public void Both_templates_carry_the_same_facts_about_the_meeting()
    {
        // The layouts differ; the record must not. Anything asserted here is something a reader
        // comparing the two files would notice missing from one of them.
        var content = Content();
        var minutes = Minutes();
        var writer = new MinutesDocumentWriter();

        var global = TextOf(writer.WriteDocx(minutes, content, MinutesTemplates.GlobalEn));
        var vietnamese = TextOf(writer.WriteDocx(minutes, content, MinutesTemplates.VnNd30));

        foreach (var fact in new[]
        {
            "BB-2026-0007",
            "Sprint review",
            "Trần Quang Huy",
            "Lê Thị Mai",
            "Ship the export on Friday",
            "Ngô Xuân Hạnh Nhi",
            "Huỳnh Thái Tú"
        })
        {
            global.Should().Contain(fact);
            vietnamese.Should().Contain(fact);
        }
    }

    [Fact]
    public void A_draft_says_so_on_its_face_in_both_templates()
    {
        var writer = new MinutesDocumentWriter();
        var draft = Minutes(status: "DRAFT");

        TextOf(writer.WriteDocx(draft, Content(), MinutesTemplates.GlobalEn))
            .Should().Contain("DRAFT");
        TextOf(writer.WriteDocx(draft, Content(), MinutesTemplates.VnNd30))
            .Should().Contain("BẢN NHÁP");
    }

    [Fact]
    public void The_global_template_dates_are_ISO_8601()
    {
        var text = TextOf(new MinutesDocumentWriter().WriteDocx(
            Minutes(), Content(), MinutesTemplates.GlobalEn));

        // A bare "2026-03-10 09:00" in an international document is ambiguous by exactly the
        // amount that matters when a deadline is read off it, so the offset is written out.
        text.Should().Contain("2026-03-10T09:00:00Z");
        text.Should().Contain("UTC+00:00");
    }

    [Fact]
    public void The_global_template_opens_with_a_document_control_block()
    {
        var text = TextOf(new MinutesDocumentWriter().WriteDocx(
            Minutes(), Content(), MinutesTemplates.GlobalEn));

        text.Should().Contain("Document ID");
        text.Should().Contain("Version");
        text.Should().Contain("Status");
    }

    [Fact]
    public void The_control_block_omits_rows_the_record_has_no_value_for()
    {
        // A control table listing a field this service cannot fill invites a reader to treat the
        // blank as meaningful. A draft has not been approved, so it has no approval row at all.
        var content = Content();
        content.ScheduledAt = null;

        var text = TextOf(new MinutesDocumentWriter().WriteDocx(
            Minutes(status: "DRAFT"), content, MinutesTemplates.GlobalEn));

        text.Should().NotContain("Scheduled for");
        text.Should().NotContain("Approved at");
    }

    [Fact]
    public void A_motion_prints_who_moved_it_and_how_it_carried()
    {
        // Robert's Rules minutes record the action taken, not only the tally. Without the mover
        // and the outcome the reader gets three numbers and no decision.
        var content = Content();
        content.Votes = new List<MinutesVote>
        {
            new()
            {
                Topic = "Adopt the Q4 budget",
                MovedBy = "Lê Thị Mai",
                SecondedBy = "Trần Quang Huy",
                Outcome = "Carried",
                ForCount = 5,
                AgainstCount = 1,
                AbstainCount = 2
            }
        };

        var text = TextOf(new MinutesDocumentWriter().WriteDocx(
            Minutes(), content, MinutesTemplates.GlobalEn));

        text.Should().Contain("Adopt the Q4 budget");
        text.Should().Contain("Moved by Lê Thị Mai");
        text.Should().Contain("seconded by Trần Quang Huy");
        text.Should().Contain("Carried");
        text.Should().Contain("In favour 5");
    }

    [Fact]
    public void An_outcome_nobody_stated_is_not_inferred_from_the_counts()
    {
        // A motion needing a two-thirds majority carries on numbers that would fail a simple one,
        // and nothing here knows which bar applied. 5-1-2 must not print as "Carried".
        var content = Content();
        content.Votes = new List<MinutesVote>
        {
            new() { Topic = "Adopt the Q4 budget", ForCount = 5, AgainstCount = 1, AbstainCount = 2 }
        };

        var text = TextOf(new MinutesDocumentWriter().WriteDocx(
            Minutes(), content, MinutesTemplates.GlobalEn));

        text.Should().Contain("In favour 5");
        text.Should().NotContain("Outcome:");
        text.Should().NotContain("Carried");
    }

    [Fact]
    public void A_seconder_nobody_named_leaves_no_dangling_clause()
    {
        // "seconded by —————" reads as a motion that failed for want of a seconder, which is a
        // different fact from one whose seconder was simply not recorded.
        var content = Content();
        content.Votes = new List<MinutesVote>
        {
            new() { Topic = "Adopt the Q4 budget", MovedBy = "Lê Thị Mai" }
        };

        var text = TextOf(new MinutesDocumentWriter().WriteDocx(
            Minutes(), content, MinutesTemplates.GlobalEn));

        text.Should().Contain("Moved by Lê Thị Mai");
        text.Should().NotContain("seconded by");
    }

    [Fact]
    public void Neither_template_invents_a_date_it_does_not_have()
    {
        // The single most dangerous thing a document generator can do to a record with legal
        // weight is to fill a missing timestamp with today.
        var content = Content();
        content.ClosedAt = null;

        var writer = new MinutesDocumentWriter();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var global = TextOf(writer.WriteDocx(Minutes(status: "DRAFT"), content, MinutesTemplates.GlobalEn));
        global.Should().NotContain(today);

        var vietnamese = TextOf(writer.WriteDocx(Minutes(status: "DRAFT"), content, MinutesTemplates.VnNd30));
        vietnamese.Should().NotContain(DateTime.UtcNow.ToString("dd/MM/yyyy"));
    }

    [Fact]
    public void The_global_sections_are_numbered_without_a_gap_when_nothing_was_moved()
    {
        // A document that runs 1, 2, 3, 5, 6 reads as one with a page missing. Motions are omitted
        // when nobody moved anything, so the numbers behind them have to close up.
        var text = TextOf(new MinutesDocumentWriter().WriteDocx(
            Minutes(), Content(), MinutesTemplates.GlobalEn));

        text.Should().Contain("4. ADJOURNMENT");
        text.Should().Contain("5. CERTIFICATION");
        text.Should().NotContain("6. ");
    }

    [Fact]
    public void Motions_take_their_own_number_and_push_the_rest_down()
    {
        var content = Content();
        content.Votes = new List<MinutesVote>
        {
            new() { Topic = "Adopt the Q4 budget", ForCount = 5, AgainstCount = 1, AbstainCount = 2 }
        };

        var text = TextOf(new MinutesDocumentWriter().WriteDocx(
            Minutes(), content, MinutesTemplates.GlobalEn));

        text.Should().Contain("4. MOTIONS AND RESOLUTIONS");
        text.Should().Contain("5. ADJOURNMENT");
        text.Should().Contain("6. CERTIFICATION");
    }

    [Fact]
    public void Markdown_in_the_narrative_is_rendered_in_both_templates()
    {
        // warptalk-ai writes the overview in Markdown. Printed raw it puts hash marks and
        // asterisks into a document somebody signs — in either layout.
        var content = Content();
        content.Sections.Insert(0, new MinutesSection
        {
            Key = "summary",
            Kind = "paragraph",
            Text = string.Join(
                Environment.NewLine,
                "## Meeting Summary",
                "- Accuracy is **80%**.",
                "  - Add **500 questions**.")
        });

        var writer = new MinutesDocumentWriter();
        foreach (var template in new[] { MinutesTemplates.GlobalEn, MinutesTemplates.VnNd30 })
        {
            var text = TextOf(writer.WriteDocx(Minutes(), content, template));

            text.Should().NotContain("#");
            text.Should().NotContain("**");
            // Rendered, not stripped: every word still reaches the page.
            text.Should().Contain("Meeting Summary");
            text.Should().Contain("Accuracy is 80%.");
            text.Should().Contain("Add 500 questions.");
        }
    }

    [Fact]
    public void The_global_certification_block_is_the_international_one()
    {
        // Rule, printed name, capacity, date — in that order, under the sentence the signature
        // attests to. The Vietnamese block puts the role above the name; this one must not.
        var text = TextOf(new MinutesDocumentWriter().WriteDocx(
            Minutes(status: "DRAFT"), Content(), MinutesTemplates.GlobalEn));

        text.Should().Contain("true and correct record");
        text.Should().Contain("Secretary");
        text.Should().Contain("Chair of the meeting");
        text.Should().Contain("Date:");
        // An unsigned block says so, rather than leaving a reader to notice an empty date.
        text.Should().Contain("(not signed)");
    }

    [Fact]
    public void A_signed_certification_carries_the_date_it_was_signed()
    {
        var text = TextOf(new MinutesDocumentWriter().WriteDocx(
            Minutes(), Content(), MinutesTemplates.GlobalEn));

        text.Should().Contain("Date: 2026-03-10");
        text.Should().NotContain("(not signed)");
    }

    [Fact]
    public void The_control_block_says_what_the_record_was_made_from_and_in_how_many_languages()
    {
        // "What is this a record OF" is the question a document control block exists to answer,
        // and a reader weighing a line needs to know before reading it whether they are looking at
        // what was said or at a machine's rendering of it.
        var content = Content();
        content.PrimaryLanguage = "vi";
        content.Translations = new Dictionary<string, List<MinutesSection>>
        {
            ["en"] = new() { new() { Key = "summary", Kind = "paragraph", Text = "Reviewed the sprint." } },
            ["ja"] = new() { new() { Key = "summary", Kind = "paragraph", Text = "スプリントを確認した。" } }
        };

        var text = TextOf(new MinutesDocumentWriter().WriteDocx(
            Minutes(), content, MinutesTemplates.GlobalEn));

        text.Should().Contain("Source record");
        text.Should().Contain("Transcript of this meeting");
        text.Should().Contain("Translations");
        text.Should().Contain("2 (en, ja)");
        text.Should().Contain("machine-translated");
    }

    [Fact]
    public void The_adjournment_records_the_hour_and_claims_nothing_else()
    {
        // "There being no further business" describes a room this service was not in, and would
        // print identically on a meeting that simply ran out of time.
        var text = TextOf(new MinutesDocumentWriter().WriteDocx(
            Minutes(), Content(), MinutesTemplates.GlobalEn));

        text.Should().Contain("The meeting was adjourned at 2026-03-10T10:05:00Z");
        text.Should().NotContain("no further business");
    }

    [Fact]
    public void Each_template_sets_the_page_up_the_way_its_form_requires()
    {
        // Nghị định 30 asks for a 30mm left margin — 1701 twips — and it is the measurement that
        // actually gets checked, because it is the binding edge on a filed document. The global
        // form uses 1 inch all round.
        static PageMargin MarginOf(byte[] docx)
        {
            using var stream = new MemoryStream(docx);
            using var document = WordprocessingDocument.Open(stream, false);
            return document.MainDocumentPart!.Document!.Body!
                .Elements<SectionProperties>().Single()
                .Elements<PageMargin>().Single();
        }

        var writer = new MinutesDocumentWriter();

        MarginOf(writer.WriteDocx(Minutes(), Content(), MinutesTemplates.VnNd30))
            .Left!.Value.Should().Be(1701U);
        MarginOf(writer.WriteDocx(Minutes(), Content(), MinutesTemplates.GlobalEn))
            .Left!.Value.Should().Be(1440U);
    }
}
