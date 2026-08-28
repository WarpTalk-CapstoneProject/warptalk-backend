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
/// WT-587 — the room setting that decides whether a meeting is written down at all.
///
/// Every test here is about the SAME risk in a different disguise: that a room ends up reported
/// as ephemeral by something other than a person choosing it. The setting lives in a jsonb blob
/// that every room created before it predates, it travels between two services, and the value
/// that loses data is the one every default in the stack would otherwise hand back.
/// </summary>
public class TranscriptRetentionSettingTests
{
    private static CreateTranslationRoomRequest Request(RoomSettingsRequest? settings = null) =>
        new(
            WorkspaceId: Guid.NewGuid(),
            Title: "Standup",
            Description: null,
            TranslationRoomType: TranslationRoomTypes.ChannelMeeting,
            MaxParticipants: null,
            SourceLanguage: "vi-VN",
            TargetLanguages: new List<string> { "en-US" },
            Settings: settings,
            ScheduledAt: null,
            InvitedEmails: null);

    private static TranslationRoomSettings Stored(CreateTranslationRoomRequest request)
    {
        var room = request.ToEntity(Guid.NewGuid(), "abc-defg-hij", "WAITING", "vi-VN", new List<string> { "en-US" });
        return JsonSerializer.Deserialize<TranslationRoomSettings>(room.Settings)!;
    }

    [Fact]
    public void ANewRoomKeepsItsTranscriptUnlessSomebodySaysOtherwise()
    {
        Stored(Request()).SaveTranscript.Should().BeTrue();
    }

    [Fact]
    public void NoMeetingTypeQuietlyTurnsRecordingOff()
    {
        // A meeting type says how a room is RUN. If one of them also decided that the meeting
        // leaves no record, a user would pick "Virtual appointment" for its seat count and lose
        // the transcript, the summary and the minutes without being told.
        foreach (var type in TranslationRoomTypes.All)
        {
            var room = Request() with { TranslationRoomType = type };
            Stored(room).SaveTranscript.Should().BeTrue($"'{type}' must not opt a room out of its own record");
        }
    }

    [Fact]
    public void AnExplicitChoiceIsHonoured()
    {
        Stored(Request(new RoomSettingsRequest(SaveTranscript: false)))
            .SaveTranscript.Should().BeFalse();
    }

    [Fact]
    public void ARoomCreatedBeforeThisSettingExistedKeepsItsTranscript()
    {
        // THE BACKFILL CASE. Every room in production has a settings blob with no save_transcript
        // key in it. Deserializing that must land on the property initializer, not on bool's
        // default — a `false` here would stop recording every meeting in the system on deploy,
        // and nothing in the pipeline would report an error while it happened.
        var legacyBlob = """{"requires_approval":true,"artifact_access":"HOST_ONLY","mute_on_entry":false}""";

        TranslationRoomMapper.ReadSettings(legacyBlob).SaveTranscript.Should().BeTrue();
    }

    [Fact]
    public void AStoredFalseSurvivesTheRoundTripToTheApi()
    {
        // ReadSettings deserializes into the DOMAIN type precisely because the blob is snake_case
        // and the response record is not — the same mismatch silently reported every room as
        // requires_approval=false once. This asserts the new key is spelled the way it is written.
        var blob = JsonSerializer.Serialize(new TranslationRoomSettings { SaveTranscript = false });

        blob.Should().Contain("save_transcript");
        TranslationRoomMapper.ReadSettings(blob).SaveTranscript.Should().BeFalse();
    }

    [Fact]
    public void AMalformedBlobKeepsTheTranscriptRatherThanDiscardingIt()
    {
        TranslationRoomMapper.ReadSettings("{ not json").SaveTranscript.Should().BeTrue();
        TranslationRoomMapper.ReadSettings(null).SaveTranscript.Should().BeTrue();
        TranslationRoomMapper.ReadSettings("").SaveTranscript.Should().BeTrue();
    }

    [Fact]
    public void AnEditThatDoesNotMentionRetentionLeavesItAlone()
    {
        // The settings update is a PATCH: null means "leave it". A host renaming an ephemeral
        // room must not turn recording back on, and a host toggling breakouts in a recorded room
        // must not turn it off.
        var ephemeral = new TranslationRoomSettings { SaveTranscript = false };
        var edit = new RoomSettingsRequest(BreakoutsEnabled: false);

        (edit.SaveTranscript ?? ephemeral.SaveTranscript).Should().BeFalse();

        var recorded = new TranslationRoomSettings { SaveTranscript = true };
        (edit.SaveTranscript ?? recorded.SaveTranscript).Should().BeTrue();
    }
}
