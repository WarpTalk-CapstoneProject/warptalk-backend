using System;
using System.Collections.Generic;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Constants;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Mappers;

/// <summary>
/// WT-662: the room detail DTO must carry attendance, not inherit the record's defaulted 0.
///
/// <c>AttendedCount</c> was added to both room DTOs and wired all the way through the web client,
/// but nothing on the backend ever assigned it on the DETAIL path — so every finished meeting
/// rendered "0 attended" in the header pill and, because the People panel reads the same field,
/// stated the wrong number twice on one page.
///
/// It could not be caught by the compiler where it was declared: <c>AttendedCount</c> is a
/// trailing record parameter followed by optional ones, and C# will not let a required parameter
/// follow an optional one, so the `= 0` cannot be removed without reordering the record and every
/// positional construction site with it. The guard therefore lives on the MAPPER, which is the only
/// way these DTOs are built — <c>attendedCount</c> is a required argument there, exactly as
/// <c>participantCount</c> has been since WT-280 and for the same stated reason.
///
/// These tests are deliberately unit-level: <c>RoomOccupancyCountTests</c> covers the repository
/// query against a real Postgres, which needs Docker, and the defect here was never in the query.
/// </summary>
public class AttendedCountMappingTests
{
    private static Domain.Entities.TranslationRoom EndedRoom() =>
        new CreateTranslationRoomRequest(
            WorkspaceId: Guid.NewGuid(),
            Title: "Retrospective",
            Description: null,
            TranslationRoomType: TranslationRoomTypes.Event,
            MaxParticipants: 100,
            SourceLanguage: "vi-VN",
            TargetLanguages: new List<string> { "en-US" },
            Settings: null,
            ScheduledAt: null,
            InvitedEmails: null)
        .ToEntity(Guid.NewGuid(), "abc-defg-hij", "ENDED", "vi-VN", new List<string> { "en-US" });

    [Fact]
    public void RoomDetail_ReportsAttendance_RatherThanTheRecordDefault()
    {
        var dto = EndedRoom().ToResponseDto(participantCount: 0, attendedCount: 12);

        dto.AttendedCount.Should().Be(12);
    }

    /// <summary>
    /// The two counts answer different questions and must not be conflated. This is the exact
    /// shape of the production symptom: the meeting is over, so every participant row has been
    /// swept CONNECTED -> DISCONNECTED and occupancy is genuinely 0, while twelve people attended.
    /// </summary>
    [Fact]
    public void EndedRoom_ReportsZeroOccupancy_AndRealAttendance()
    {
        var dto = EndedRoom().ToResponseDto(participantCount: 0, attendedCount: 12);

        dto.ParticipantCount.Should().Be(0, "nobody holds a seat once the room has ended");
        dto.AttendedCount.Should().Be(12, "attendance is the only figure a finished meeting has");
    }

    /// <summary>
    /// A live room answers both, and they are independent: somebody who joined and left counts as
    /// attended but holds no seat, so attendance is greater than or equal to occupancy — never
    /// derived from it.
    /// </summary>
    [Fact]
    public void LiveRoom_CarriesBothCounts_Independently()
    {
        var dto = EndedRoom().ToResponseDto(participantCount: 3, attendedCount: 5);

        dto.ParticipantCount.Should().Be(3);
        dto.AttendedCount.Should().Be(5);
    }

    [Fact]
    public void HistoryDto_CarriesAttendance_Too()
    {
        var dto = EndedRoom().ToHistoryDto(participantCount: 0, attendedCount: 7);

        dto.AttendedCount.Should().Be(7);
    }
}
