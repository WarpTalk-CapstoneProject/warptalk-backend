using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Entities;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// What a biên bản draft must get right before a person is asked to sign it.
///
/// The attendance half is the part with no room for judgement: who was here, who was invited and
/// did not come, and whether that made quorum are facts the meeting recorded, and a signed
/// document that states them wrongly is worse than one that omits them. The narrative half is the
/// opposite — it comes from the summary verbatim, and the tests here are mostly about what the
/// drafter must REFUSE to carry through.
/// </summary>
public class MeetingMinutesDrafterTests
{
    private static readonly DateTime Opened = new(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Closed = new(2026, 8, 20, 10, 5, 0, DateTimeKind.Utc);

    private static TranslationRoom Room() => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        Title = "Sprint review",
        SourceLanguage = "vi",
        EndedAt = Closed,
        ScheduledAt = Opened.AddMinutes(-5)
    };

    private static TranslationRoomParticipant Person(
        string name,
        string status,
        DateTime? joinedAt = null,
        bool isExternal = false,
        string role = "PARTICIPANT") => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = name,
        Status = status,
        Role = role,
        JoinedAt = joinedAt,
        IsExternal = isExternal,
        SpeakLanguage = "vi",
        ListenLanguage = "vi"
    };

    private static MeetingMinutesContent Parse(string json) =>
        JsonSerializer.Deserialize<MeetingMinutesContent>(json)!;

    [Fact]
    public void SomebodyInvitedWhoNeverJoinedIsRecordedAsAbsent()
    {
        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(),
            new List<TranslationRoomParticipant>
            {
                Person("Tú", "CONNECTED", Opened),
                Person("Nhi", "INVITED")
            },
            null));

        content.Attendance.Present.Should().ContainSingle(p => p.Name == "Tú");
        content.Attendance.Absent.Should().ContainSingle(p => p.Name == "Nhi");
        content.Attendance.InvitedCount.Should().Be(2);
        content.Attendance.PresentCount.Should().Be(1);
    }

    [Fact]
    public void SomebodyWhoseConnectionDroppedStillAttended()
    {
        // Leaving them out would understate the room, and quorum is counted off this list.
        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(),
            new List<TranslationRoomParticipant>
            {
                Person("Tú", "CONNECTED", Opened),
                Person("Kỳ", "DISCONNECTED", Opened.AddMinutes(2)),
                Person("Tuấn", "LEFT", Opened.AddMinutes(3))
            },
            null));

        content.Attendance.Present.Should().HaveCount(3);
        content.Attendance.Absent.Should().BeEmpty();
    }

    [Fact]
    public void QuorumIsAMajorityOfThoseInvitedAndSaysSoInWords()
    {
        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(),
            new List<TranslationRoomParticipant>
            {
                Person("A", "CONNECTED", Opened),
                Person("B", "CONNECTED", Opened),
                Person("C", "CONNECTED", Opened),
                Person("D", "INVITED"),
                Person("E", "INVITED")
            },
            null));

        content.Attendance.QuorumMet.Should().BeTrue();
        // A bare boolean would not tell the reader what bar was applied.
        content.Attendance.QuorumRule.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ExactlyHalfIsNotAMajority()
    {
        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(),
            new List<TranslationRoomParticipant>
            {
                Person("A", "CONNECTED", Opened),
                Person("B", "CONNECTED", Opened),
                Person("C", "INVITED"),
                Person("D", "INVITED")
            },
            null));

        content.Attendance.QuorumMet.Should().BeFalse();
    }

    [Fact]
    public void ARoomNobodyWasInvitedToHasNoQuorumAnswerRatherThanAFalseOne()
    {
        // An ad-hoc room has no roll to be a majority of. Answering "false" would be a claim.
        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(), new List<TranslationRoomParticipant>(), null));

        content.Attendance.QuorumMet.Should().BeNull();
        content.Attendance.QuorumRule.Should().BeNull();
    }

    [Fact]
    public void TheMeetingOpensWhenTheFirstPersonJoinedAndClosesWhenTheRoomEnded()
    {
        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(),
            new List<TranslationRoomParticipant>
            {
                Person("Late", "CONNECTED", Opened.AddMinutes(12)),
                Person("First", "CONNECTED", Opened),
                Person("Middle", "CONNECTED", Opened.AddMinutes(4))
            },
            null));

        content.OpenedAt.Should().Be(Opened);
        content.ClosedAt.Should().Be(Closed);
        // Kept beside the real opening time, because "started late" is itself a fact.
        content.ScheduledAt.Should().Be(Opened.AddMinutes(-5));
    }

    [Fact]
    public void AMeetingNobodyJoinedHasNoOpeningTime()
    {
        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(),
            new List<TranslationRoomParticipant> { Person("Nobody", "INVITED") },
            null));

        content.OpenedAt.Should().BeNull();
    }

    [Fact]
    public void ExternalGuestsAndSpokenLanguageSurviveOntoTheRecord()
    {
        // On a bilingual record the language is what tells a reader which half of a quoted
        // decision is the original, so it is not decoration.
        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(),
            new List<TranslationRoomParticipant>
            {
                Person("Khách", "CONNECTED", Opened, isExternal: true)
            },
            null));

        content.Attendance.Present[0].IsExternal.Should().BeTrue();
        content.Attendance.Present[0].SpeakLanguage.Should().Be("vi");
    }

    [Fact]
    public void SummarySectionsCarryTheirCitations()
    {
        var summary = """
        {
          "summary": "Reviewed the sprint.",
          "decisions": [{"text": "Ship on Friday", "atMs": 120000}],
          "actionItems": [{"task": "Write the release note", "owner": "Nhi", "atMs": 300000}]
        }
        """;

        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(), new List<TranslationRoomParticipant>(), summary));

        content.Sections.Should().Contain(s => s.Key == "summary" && s.Text == "Reviewed the sprint.");

        var decisions = content.Sections.Find(s => s.Key == "decisions")!;
        decisions.Items!.Should().ContainSingle();
        decisions.Items![0].Text.Should().Be("Ship on Friday");
        // The citation is what lets a reader check a signed line against the transcript.
        decisions.Items![0].AtMs.Should().Be(120000);

        var actions = content.Sections.Find(s => s.Key == "actionItems")!;
        actions.Items![0].Text.Should().Be("Write the release note");
        actions.Items![0].Owner.Should().Be("Nhi");
    }

    [Fact]
    public void SummariesWrittenBeforeCitationsExistedStillRead()
    {
        // Older artifacts stored bare strings. They are still the meeting's decisions.
        var summary = """{"summary": "Short meeting.", "decisions": ["Ship on Friday"]}""";

        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(), new List<TranslationRoomParticipant>(), summary));

        var decisions = content.Sections.Find(s => s.Key == "decisions")!;
        decisions.Items![0].Text.Should().Be("Ship on Friday");
        decisions.Items![0].AtMs.Should().BeNull();
    }

    [Fact]
    public void AnInsufficientDataSummaryContributesNothingToTheRecord()
    {
        // Its "summary" is a status message. Under a heading, in a signed document, it would read
        // as a finding about the meeting.
        var summary = """
        {"summary": "The AI assistant could not generate a summary.", "insufficientData": true}
        """;

        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(), new List<TranslationRoomParticipant>(), summary));

        content.Sections.Should().BeEmpty();
    }

    [Fact]
    public void ASummaryThatIsPlainTextBecomesTheOverviewRatherThanBeingDropped()
    {
        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(), new List<TranslationRoomParticipant>(), "We agreed to ship on Friday."));

        content.Sections.Should().ContainSingle();
        content.Sections[0].Key.Should().Be("summary");
        content.Sections[0].Text.Should().Be("We agreed to ship on Friday.");
    }

    [Fact]
    public void VotesAreNeverInferredFromTheTranscript()
    {
        // Silence is not assent. A tally has to come from people pressing a button.
        var summary = """
        {"summary": "Everyone agreed.", "decisions": [{"text": "All in favour", "atMs": 1000}]}
        """;

        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(), new List<TranslationRoomParticipant>(), summary));

        content.Votes.Should().BeEmpty();
    }

    [Fact]
    public void TheAgendaIsLeftForTheSecretaryRatherThanGuessedAt()
    {
        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(), new List<TranslationRoomParticipant>(), """{"summary": "Talked."}"""));

        content.Agenda.Should().BeNull();
    }

    [Fact]
    public void TheLanguageTheMeetingWasHeldInIsRecorded()
    {
        // Without it a bilingual document cannot say which half is the original, and a reader is
        // left inferring it from the script.
        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(), new List<TranslationRoomParticipant>(), """{"summary": "Đã họp."}"""));

        content.PrimaryLanguage.Should().Be("vi");
    }

    [Fact]
    public void TranslatedSectionsAreCarriedThroughInTheSameShape()
    {
        var summary = """
        {
          "summary": "Đã rà soát sprint.",
          "decisions": [{"text": "Phát hành thứ Sáu", "atMs": 120000}],
          "translations": {
            "en": {
              "summary": "Reviewed the sprint.",
              "decisions": [{"text": "Ship on Friday", "atMs": 120000}]
            }
          }
        }
        """;

        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(), new List<TranslationRoomParticipant>(), summary));

        content.Translations.Should().ContainKey("en");
        var english = content.Translations!["en"];
        english.Should().Contain(section => section.Key == "summary" && section.Text == "Reviewed the sprint.");
        english.Find(section => section.Key == "decisions")!.Items![0].Text.Should().Be("Ship on Friday");
        // The citation must survive: it is the only join key between the two languages.
        english.Find(section => section.Key == "decisions")!.Items![0].AtMs.Should().Be(120000);
    }

    [Fact]
    public void ASingleLanguageMeetingSimplyHasNoTranslations()
    {
        // The summary worker is only asked for translations when a room has more than one target
        // language, so their absence is the normal case and must not read as an empty set.
        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(), new List<TranslationRoomParticipant>(), """{"summary": "Đã họp."}"""));

        content.Translations.Should().BeNull();
    }

    [Fact]
    public void AnInsufficientDataSummaryContributesNoTranslationsEither()
    {
        var summary = """
        {"summary": "No transcript content.", "insufficientData": true,
         "translations": {"en": {"summary": "No transcript content."}}}
        """;

        var content = Parse(MeetingMinutesDrafter.BuildContent(
            Room(), new List<TranslationRoomParticipant>(), summary));

        content.Translations.Should().BeNull();
    }

    [Fact]
    public void AnUntouchedDraftCountsAsZeroEdits()
    {
        var draft = MeetingMinutesDrafter.BuildContent(
            Room(),
            new List<TranslationRoomParticipant> { Person("Tú", "CONNECTED", Opened) },
            """{"summary": "Reviewed.", "decisions": [{"text": "Ship", "atMs": 1}]}""");

        MeetingMinutesDrafter.CountEdits(draft, draft).Should().Be(0);
    }

    [Fact]
    public void EditingTheDraftIsCountedSoAReaderKnowsAPersonReadIt()
    {
        var room = Room();
        var people = new List<TranslationRoomParticipant> { Person("Tú", "CONNECTED", Opened) };
        var draft = MeetingMinutesDrafter.BuildContent(
            room, people, """{"summary": "Reviewed.", "decisions": [{"text": "Ship", "atMs": 1}]}""");

        var edited = Parse(draft);
        edited.Agenda = "1. Sprint review";
        edited.Sections.Find(s => s.Key == "decisions")!.Items![0].Text = "Ship on Friday";

        MeetingMinutesDrafter.CountEdits(draft, JsonSerializer.Serialize(edited))
            .Should().BeGreaterThan(0);
    }
}
