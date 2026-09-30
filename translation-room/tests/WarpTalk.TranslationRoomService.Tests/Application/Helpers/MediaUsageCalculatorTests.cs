using System;
using System.Linq;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>LiveKit usage per UTC hour and workspace for the admin Providers page (GetMediaUsage RPC).</summary>
public class MediaUsageCalculatorTests
{
    private static readonly DateTime From = new(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 9, 25, 6, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Workspace = Guid.NewGuid();

    [Fact]
    public void A_meeting_across_an_hour_boundary_is_split_between_the_hours()
    {
        var room = Guid.NewGuid();
        var rows = MediaUsageCalculator.Hourly(
            [new MediaUsageRoomSpan(room, Workspace, From.AddHours(9).AddMinutes(30), From.AddHours(10).AddMinutes(15), null, "ENDED")],
            [
                new MediaUsageParticipantSpan(room, From.AddHours(9).AddMinutes(30), From.AddHours(10).AddMinutes(15)),
                // Joined before the room started, never left: clipped to the room.
                new MediaUsageParticipantSpan(room, From.AddHours(9), null),
            ],
            [new MediaUsageRecording(Workspace, From.AddHours(10).AddMinutes(20), 1_000)],
            From, To, Now);

        rows.Should().HaveCount(2);
        rows[0].HourStart.Should().Be(From.AddHours(9));
        rows[0].RoomSeconds.Should().Be(1800);
        rows[0].ParticipantSeconds.Should().Be(3600);
        rows[0].RoomsStarted.Should().Be(1);
        rows[1].RoomSeconds.Should().Be(900);
        rows[1].ParticipantSeconds.Should().Be(1800);
        rows[1].Recordings.Should().Be(1);
        rows[1].RecordingBytes.Should().Be(1_000);
    }

    [Fact]
    public void A_live_room_runs_to_now_capped_like_the_insights_hours_and_the_window_clips_it()
    {
        var room = Guid.NewGuid();
        var rows = MediaUsageCalculator.Hourly(
            [new MediaUsageRoomSpan(room, Workspace, From.AddHours(-2), null, null, "IN_PROGRESS")],
            [],
            [],
            From, From.AddHours(3), From.AddHours(2).AddMinutes(30));

        rows.Sum(r => r.RoomSeconds).Should().Be(2.5 * 3600, "the live room runs to now, and only its part inside the window counts");
        rows.Should().NotContain(r => r.RoomsStarted > 0, "it started before the window");
    }

    [Fact]
    public void A_finished_room_with_no_end_and_no_duration_is_left_out_of_the_minutes()
    {
        var rows = MediaUsageCalculator.Hourly(
            [new MediaUsageRoomSpan(Guid.NewGuid(), Workspace, From.AddHours(1), null, null, "ENDED")],
            [],
            [],
            From, To, Now);

        rows.Should().ContainSingle().Which.RoomSeconds.Should().Be(0);
    }
}
