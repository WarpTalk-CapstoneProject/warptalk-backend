using System;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Mappers;

/// <summary>
/// WT-446: the roster says who is a guest.
///
/// Someone invited into a room by link or email can already read and join it — RoomReadAccess has
/// allowed host / participant / invited-by-email for a long time, with no workspace branch. What
/// was missing was any way to TELL, from inside the meeting, that the person you are talking to is
/// not one of your colleagues. The People panel had the badge built and was rendering it against a
/// field the API never sent.
///
/// Externality is resolved once per admission, in JoinTranslationRoomAsync, and stored — the
/// roster is polled every three seconds and this is a fact about being let in, not a live property.
/// </summary>
public class ExternalParticipantTests
{
    private static JoinTranslationRoomRequest Request() =>
        new("ABC123", "Guest", "vi", "en");

    [Fact]
    public void AGuestIsRecordedAsExternalOnTheirFirstJoin()
    {
        var participant = Request().ToParticipantEntity(
            Guid.NewGuid(),
            Guid.NewGuid(),
            speakLanguage: "en",
            listenLanguage: "vi",
            requiresApproval: false,
            isHost: false,
            isExternal: true);

        participant.IsExternal.Should().BeTrue();
    }

    [Fact]
    public void AMemberIsNotExternal()
    {
        var participant = Request().ToParticipantEntity(
            Guid.NewGuid(),
            Guid.NewGuid(),
            speakLanguage: "en",
            listenLanguage: "vi",
            requiresApproval: false,
            isHost: false,
            isExternal: false);

        participant.IsExternal.Should().BeFalse();
    }

    [Fact]
    public void TheDefaultIsNotExternal()
    {
        // Every caller that predates WT-446 omits the argument, and none of them may start
        // labelling their participants guests.
        var participant = Request().ToParticipantEntity(
            Guid.NewGuid(),
            Guid.NewGuid(),
            speakLanguage: "en",
            listenLanguage: "vi",
            requiresApproval: false,
            isHost: false);

        participant.IsExternal.Should().BeFalse();
    }

    [Fact]
    public void JoiningTheWorkspaceStopsYouBeingAGuestOnTheNextAdmission()
    {
        // Refreshed rather than frozen at first admission: someone added to the workspace since
        // their last visit is a colleague now, and a roster still calling them External would be
        // stating something that stopped being true.
        var participant = new TranslationRoomParticipant
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            DisplayName = "Guest",
            Role = "PARTICIPANT",
            ListenLanguage = "vi",
            SpeakLanguage = "en",
            Status = TranslationRoomParticipantStatuses.Left,
            IsExternal = true,
        };

        participant.UpdateFrom(
            Request(),
            speakLanguage: "en",
            listenLanguage: "vi",
            requiresApproval: false,
            isHost: false,
            isExternal: false);

        participant.IsExternal.Should().BeFalse();
    }

    [Fact]
    public void TheRosterCarriesItToTheClient()
    {
        // The People panel's badge reads exactly this field. It was built before anything set it.
        var participant = new TranslationRoomParticipant
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            DisplayName = "Guest",
            Role = "PARTICIPANT",
            ListenLanguage = "vi",
            SpeakLanguage = "en",
            Status = TranslationRoomParticipantStatuses.Connected,
            IsExternal = true,
        };

        participant.ToDto().IsExternal.Should().BeTrue();
    }
}
