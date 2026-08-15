using System;
using System.Collections.Generic;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Constants;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Mappers;

/// <summary>
/// An EXTERNAL_BRIDGE room translates for a call happening somewhere else — Google Meet, Zoom —
/// by holding two seats: the WarpTalk user, and one stand-in for everyone on the far side.
///
/// Nothing downstream is taught about this arrangement. The audio mesh pairs a source's
/// SpeakLanguage with a target's ListenLanguage, so getting these four language fields right is
/// the entire mechanism: wrong, and the mesh emits a route that translates a language into
/// itself, which reaches production as "the other side is mute" rather than as an error.
/// </summary>
public class ExternalBridgeParticipantTests
{
    private const string Source = "vi-VN";
    private static readonly IReadOnlyList<string> Targets = new List<string> { "en-US", "ja-JP" };

    private static (Domain.Entities.TranslationRoomParticipant Host, Domain.Entities.TranslationRoomParticipant FarSide) Pair()
    {
        var roomId = Guid.NewGuid();
        return (
            TranslationRoomMapper.BuildHostParticipant(
                roomId, Guid.NewGuid(), "Tú", Source, Targets, TranslationRoomTypes.ExternalBridge),
            TranslationRoomMapper.BuildExternalBridgeParticipant(roomId, Source, Targets));
    }

    [Fact]
    public void ThePairShouldProduceOneTranslatedRouteInEachDirection()
    {
        var (host, farSide) = Pair();

        // Outbound: what the host says, rendered for the far side to hear.
        host.SpeakLanguage.Should().Be(Source);
        farSide.ListenLanguage.Should().Be("en-US");

        // Inbound: what the far side says, rendered for the host to hear.
        farSide.SpeakLanguage.Should().Be("en-US");
        host.ListenLanguage.Should().Be(Source);

        // Neither direction may collapse to a no-op.
        host.SpeakLanguage.Should().NotBe(farSide.ListenLanguage);
        farSide.SpeakLanguage.Should().NotBe(host.ListenLanguage);
    }

    [Fact]
    public void TheHostShouldStillHearTheTargetLanguageInEveryOtherRoomType()
    {
        // The bridge rule inverts the seeding, so guard the case it inverts away from.
        var host = TranslationRoomMapper.BuildHostParticipant(
            Guid.NewGuid(), Guid.NewGuid(), "Tú", Source, Targets, TranslationRoomTypes.Event);

        host.ListenLanguage.Should().Be("en-US");
    }

    [Fact]
    public void TheHostShouldStillHearTheTargetLanguageWhenNoTypeIsPassed()
    {
        // Every pre-existing caller omits the argument; none of them may shift behaviour.
        var host = TranslationRoomMapper.BuildHostParticipant(
            Guid.NewGuid(), Guid.NewGuid(), "Tú", Source, Targets);

        host.ListenLanguage.Should().Be("en-US");
    }

    [Fact]
    public void TheStandInShouldCarryAResolvableUserId()
    {
        // TranslationRoomAudioRouteMapper.ToDto publishes this to the AI workers, and tts_worker
        // matches its speaker_id against it. Null here is not a cosmetic gap: it makes the
        // inbound route unmatchable and silently drops every translation from the far side.
        var (_, farSide) = Pair();

        farSide.UserId.Should().NotBeNull();
        farSide.UserId.Should().Be(TranslationRoomConstants.ExternalBridgeParticipantUserId);
        farSide.UserId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void TheStandInShouldNeverBeVoiceCloned()
    {
        // Nobody on the far side of the external call agreed to anything with WarpTalk.
        var (_, farSide) = Pair();

        farSide.IsUsingVoiceClone.Should().BeFalse();
    }

    [Fact]
    public void TheStandInShouldBeDistinguishableFromARealParticipant()
    {
        var (host, farSide) = Pair();

        farSide.ConnectionType.Should().Be(TranslationRoomMapper.ExternalBridgeConnectionType);
        host.ConnectionType.Should().NotBe(TranslationRoomMapper.ExternalBridgeConnectionType);
        farSide.DisplayName.Should().Be(TranslationRoomConstants.ExternalBridgeDisplayName);
    }

    [Fact]
    public void BothSeatsShouldHoldTheirSeatSoTheMeshSeesThem()
    {
        // GenerateRoutesAsync builds from seat holders. A stand-in that does not hold one
        // produces a room with no routes at all.
        var (host, farSide) = Pair();

        host.Status.Should().Be(TranslationRoomParticipantStatuses.Connected);
        farSide.Status.Should().Be(TranslationRoomParticipantStatuses.Connected);
    }
}
