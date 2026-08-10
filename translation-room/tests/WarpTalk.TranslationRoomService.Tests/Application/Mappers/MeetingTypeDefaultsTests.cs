using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Mappers;

/// <summary>
/// The meeting type used to be cosmetic: the UI offered six of them and the backend stored
/// only INSTANT or SCHEDULED, so every room came out configured identically. These pin the
/// agreed profile for each type, and the precedence rule that an explicit request still wins.
/// </summary>
public class MeetingTypeDefaultsTests
{
    private static CreateTranslationRoomRequest Request(
        string type,
        int? maxParticipants = null,
        RoomSettingsRequest? settings = null) =>
        new(
            WorkspaceId: Guid.NewGuid(),
            Title: "Standup",
            Description: null,
            TranslationRoomType: type,
            MaxParticipants: maxParticipants,
            SourceLanguage: "vi-VN",
            TargetLanguages: new List<string> { "en-US" },
            Settings: settings,
            ScheduledAt: null,
            InvitedEmails: null);

    private static TranslationRoomSettings SettingsOf(Domain.Entities.TranslationRoom room) =>
        JsonSerializer.Deserialize<TranslationRoomSettings>(room.Settings)!;

    private static Domain.Entities.TranslationRoom Build(
        CreateTranslationRoomRequest request,
        bool? workspaceApprovalDefault = null) =>
        request.ToEntity(
            Guid.NewGuid(),
            "abc-defg-hij",
            "WAITING",
            "vi-VN",
            new List<string> { "en-US" },
            workspaceApprovalDefault);

    // language=none — the agreed matrix, one row per type.
    public static TheoryData<string, bool, bool, bool, bool, int> Matrix => new()
    {
        //                                        approval, mute,  record, breakouts, seats
        { TranslationRoomTypes.Event,               false,  false, false,  true,      100 },
        { TranslationRoomTypes.ChannelMeeting,      false,  false, false,  true,       50 },
        { TranslationRoomTypes.Webinar,             true,   true,  true,   false,     500 },
        { TranslationRoomTypes.CompanyMeeting,      false,  true,  true,   true,      500 },
        { TranslationRoomTypes.VirtualAppointment,  true,   false, false,  false,       2 },
        { TranslationRoomTypes.LiveEvent,           true,   true,  true,   false,    1000 },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void ToEntity_ShouldSeedTheAgreedProfile_ForEachMeetingType(
        string type, bool approval, bool mute, bool record, bool breakouts, int seats)
    {
        var room = Build(Request(type));

        room.TranslationRoomType.Should().Be(type);
        room.MaxParticipants.Should().Be(seats);

        var settings = SettingsOf(room);
        settings.RequiresApproval.Should().Be(approval);
        settings.MuteOnEntry.Should().Be(mute);
        settings.AutoRecord.Should().Be(record);
        settings.BreakoutsEnabled.Should().Be(breakouts);
    }

    [Theory]
    [InlineData(TranslationRoomTypes.LegacyInstant)]
    [InlineData(TranslationRoomTypes.LegacySchedule)]
    public void ToEntity_ShouldStillAcceptTheTwoLegacyTypes(string legacyType)
    {
        // 40 production rooms carry these. They must keep working and behave like Event.
        var room = Build(Request(legacyType));

        room.TranslationRoomType.Should().Be(legacyType);
        room.MaxParticipants.Should().Be(100);
        SettingsOf(room).RequiresApproval.Should().BeFalse();
    }

    [Theory]
    [InlineData("Channel Meeting", TranslationRoomTypes.ChannelMeeting)]
    [InlineData("channel-meeting", TranslationRoomTypes.ChannelMeeting)]
    [InlineData("virtual appointment", TranslationRoomTypes.VirtualAppointment)]
    [InlineData("WEBINAR", TranslationRoomTypes.Webinar)]
    public void Normalize_ShouldFoldTheSpellingsAClientMightSend(string input, string expected)
    {
        TranslationRoomTypes.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Standup")]
    [InlineData("EVENT_2")]
    public void Normalize_ShouldReturnNull_ForAnythingUnrecognised(string input)
    {
        TranslationRoomTypes.Normalize(input).Should().BeNull();
    }

    [Fact]
    public void ToEntity_ShouldDefaultToEvent_WhenNoTypeIsGiven()
    {
        var room = Build(Request(""));

        room.TranslationRoomType.Should().Be(TranslationRoomTypes.Event);
    }

    [Fact]
    public void ToEntity_ShouldLetAnExplicitSettingWin_OverTheTypeDefault()
    {
        // A Webinar mutes on entry by default; a host who says otherwise must be obeyed.
        var room = Build(Request(
            TranslationRoomTypes.Webinar,
            settings: new RoomSettingsRequest(MuteOnEntry: false)));

        var settings = SettingsOf(room);
        settings.MuteOnEntry.Should().BeFalse();
        // Everything they did NOT mention still comes from the type.
        settings.RequiresApproval.Should().BeTrue();
        settings.AutoRecord.Should().BeTrue();
    }

    [Fact]
    public void ToEntity_ShouldTellApart_NotSentFromSentFalse()
    {
        // The whole reason RoomSettingsRequest is nullable. Sending nothing must not read as
        // "everything off", or the type could never seed anything.
        var seeded = SettingsOf(Build(Request(TranslationRoomTypes.LiveEvent, settings: null)));
        var explicitlyOff = SettingsOf(Build(Request(
            TranslationRoomTypes.LiveEvent,
            settings: new RoomSettingsRequest(RequiresApproval: false, MuteOnEntry: false, AutoRecord: false))));

        seeded.RequiresApproval.Should().BeTrue();
        explicitlyOff.RequiresApproval.Should().BeFalse();
        explicitlyOff.MuteOnEntry.Should().BeFalse();
        explicitlyOff.AutoRecord.Should().BeFalse();
    }

    // ── WT-342: the workspace default, between the two layers that already existed ──────

    /// <summary>
    /// The whole point of the ticket. EnforceHostApprovalDefault had a working toggle on the
    /// workspace settings page and a value in the settings blob, and NOTHING read it — an admin
    /// could turn it on, watch it save, reload and see it on, and every meeting created afterwards
    /// ignored it. EVENT seeds approval OFF, so a workspace asking for approval must flip it.
    /// </summary>
    [Fact]
    public void ToEntity_ShouldLetTheWorkspaceDefault_OverrideTheTypeDefault()
    {
        var room = Build(Request(TranslationRoomTypes.Event), workspaceApprovalDefault: true);

        SettingsOf(room).RequiresApproval.Should().BeTrue();
    }

    /// <summary>
    /// And in the other direction, which is what makes it a setting rather than a ratchet. A
    /// workspace that turns it off opens up a WEBINAR its type would have gated — deliberate, and
    /// never silent: the create dialog shows the resolved value on the toggle before Create, and
    /// the host can flip it for that one meeting.
    /// </summary>
    [Fact]
    public void ToEntity_ShouldLetTheWorkspaceDefault_RelaxTheTypeDefault()
    {
        var room = Build(Request(TranslationRoomTypes.Webinar), workspaceApprovalDefault: false);

        SettingsOf(room).RequiresApproval.Should().BeFalse();
        // Only approval is the workspace's business. The rest of the Webinar profile stands.
        SettingsOf(room).MuteOnEntry.Should().BeTrue();
        SettingsOf(room).AutoRecord.Should().BeTrue();
    }

    /// <summary>
    /// The creator still outranks the workspace. Without this the per-meeting toggle would be
    /// decorative in any workspace whose admin had an opinion.
    /// </summary>
    [Fact]
    public void ToEntity_ShouldLetAnExplicitChoice_OutrankTheWorkspaceDefault()
    {
        var room = Build(
            Request(TranslationRoomTypes.Event, settings: new RoomSettingsRequest(RequiresApproval: false)),
            workspaceApprovalDefault: true);

        SettingsOf(room).RequiresApproval.Should().BeFalse();
    }

    /// <summary>
    /// Null is "we could not ask", not "false". WorkspaceService being unreachable must leave the
    /// meeting type in charge, exactly as before WT-342 — otherwise an outage would quietly strip
    /// approval from every meeting created during it, which is a security decision made by a
    /// network error.
    /// </summary>
    [Fact]
    public void ToEntity_ShouldFallBackToTheType_WhenTheWorkspaceHasNoAnswer()
    {
        SettingsOf(Build(Request(TranslationRoomTypes.Webinar), workspaceApprovalDefault: null))
            .RequiresApproval.Should().BeTrue();
        SettingsOf(Build(Request(TranslationRoomTypes.Event), workspaceApprovalDefault: null))
            .RequiresApproval.Should().BeFalse();
    }

    [Fact]
    public void ToEntity_ShouldHonourAnExplicitSeatCount_OverTheTypeDefault()
    {
        var room = Build(Request(TranslationRoomTypes.VirtualAppointment, maxParticipants: 8));

        room.MaxParticipants.Should().Be(8);
    }

    [Fact]
    public void ReadSettings_ShouldReadTheSnakeCaseBlobTheServiceWrites()
    {
        // Regression: the response record is PascalCase with no [JsonPropertyName], and
        // PropertyNameCaseInsensitive does not bridge an underscore — so deserializing the
        // stored blob straight into it silently produced all-default settings.
        var stored = JsonSerializer.Serialize(new TranslationRoomSettings
        {
            RequiresApproval = true,
            ArtifactAccess = "ALL_PARTICIPANTS",
            MuteOnEntry = true,
            AutoRecord = true,
            BreakoutsEnabled = false,
        });

        var read = TranslationRoomMapper.ReadSettings(stored);

        read.RequiresApproval.Should().BeTrue();
        read.ArtifactAccess.Should().Be("ALL_PARTICIPANTS");
        read.MuteOnEntry.Should().BeTrue();
        read.AutoRecord.Should().BeTrue();
        read.BreakoutsEnabled.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    public void ReadSettings_ShouldFallBackToDefaults_RatherThanThrow(string? blob)
    {
        var read = TranslationRoomMapper.ReadSettings(blob);

        read.RequiresApproval.Should().BeTrue();
        read.ArtifactAccess.Should().Be("HOST_ONLY");
    }
}
