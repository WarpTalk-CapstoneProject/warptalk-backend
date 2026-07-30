using System;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

public class IcsCalendarBuilderTests
{
    private static readonly DateTime ScheduledAt = new(2026, 8, 1, 15, 30, 0, DateTimeKind.Utc);
    private const string JoinLink = "https://warptalk.vn/room/ABC123XYZ0";

    [Fact]
    public void Build_ProducesValidVCalendarEnvelope()
    {
        var ics = IcsCalendarBuilder.Build("room-1@warptalk.vn", "Weekly Sync", "Agenda here", ScheduledAt, JoinLink);

        ics.Should().StartWith("BEGIN:VCALENDAR\r\n");
        ics.Should().Contain("VERSION:2.0\r\n");
        ics.Should().Contain("BEGIN:VEVENT\r\n");
        ics.Should().Contain("END:VEVENT\r\n");
        ics.Should().EndWith("END:VCALENDAR\r\n");
    }

    [Fact]
    public void Build_SetsDtStartFromScheduledAt_AndDtEndOneHourLater_ByDefault()
    {
        var ics = IcsCalendarBuilder.Build("room-1@warptalk.vn", "Weekly Sync", null, ScheduledAt, JoinLink);

        ics.Should().Contain("DTSTART:20260801T153000Z\r\n");
        ics.Should().Contain("DTEND:20260801T163000Z\r\n");
    }

    [Fact]
    public void Build_UsesGivenDuration_WhenProvided()
    {
        var ics = IcsCalendarBuilder.Build("room-1@warptalk.vn", "Weekly Sync", null, ScheduledAt, JoinLink, duration: TimeSpan.FromMinutes(30));

        ics.Should().Contain("DTSTART:20260801T153000Z\r\n");
        ics.Should().Contain("DTEND:20260801T160000Z\r\n");
    }

    [Fact]
    public void Build_IncludesSummaryAndJoinLink()
    {
        var ics = IcsCalendarBuilder.Build("room-1@warptalk.vn", "Weekly Sync", null, ScheduledAt, JoinLink);

        ics.Should().Contain("SUMMARY:Weekly Sync\r\n");
        ics.Should().Contain($"URL:{JoinLink}\r\n");
        ics.Should().Contain(JoinLink); // also embedded in DESCRIPTION
    }

    [Fact]
    public void Build_FallsBackToJoinLinkOnlyDescription_WhenNoDescriptionGiven()
    {
        var ics = IcsCalendarBuilder.Build("room-1@warptalk.vn", "Weekly Sync", null, ScheduledAt, JoinLink);

        ics.Should().Contain($"DESCRIPTION:Join link: {JoinLink}\r\n");
    }

    [Fact]
    public void Build_EscapesCommasSemicolonsAndNewlinesInText_PerRfc5545()
    {
        var ics = IcsCalendarBuilder.Build("room-1@warptalk.vn", "Standup; Planning, Review\nFollow-up", null, ScheduledAt, JoinLink);

        ics.Should().Contain("SUMMARY:Standup\\; Planning\\, Review\\nFollow-up\r\n");
    }

    [Fact]
    public void Build_UsesGivenUidVerbatim()
    {
        var uid = "11111111-1111-1111-1111-111111111111@warptalk.vn";

        var ics = IcsCalendarBuilder.Build(uid, "Weekly Sync", null, ScheduledAt, JoinLink);

        ics.Should().Contain($"UID:{uid}\r\n");
    }
}
