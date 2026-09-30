using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Authorization;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// These tests used to pass while the helper they cover had never once worked.
/// </summary>
/// <remarks>
/// Every case fed the helper a hand-written settings blob containing <c>"HostOnly"</c>,
/// <c>"Participants"</c> or <c>"Workspace"</c> — the <c>nameof</c> spellings of a PascalCase enum,
/// which matched the comparison in the helper exactly and therefore proved it correct. No writer
/// in the system has ever produced any of those three strings: the persisted vocabulary is
/// <c>HOST_ONLY</c> and <c>ALL_PARTICIPANTS</c>. The suite and the code agreed with each other and
/// both disagreed with the database, so a host who opened a room to its participants still saw
/// every one of them refused, and nothing went red.
///
/// So the settings JSON here is no longer hand-written. It is produced by serializing the real
/// <see cref="TranslationRoomSettings"/> with the real <see cref="ArtifactAccessLevels"/> values —
/// the same two steps the write path takes — so a future change to either the property name or the
/// vocabulary breaks these tests instead of quietly passing them.
/// </remarks>
public class ArtifactAccessHelperTests
{
    [Fact]
    public void Host_AlwaysHasAccess_EvenOnAHostOnlyRoom()
    {
        var hostId = Guid.NewGuid();
        var room = CreateRoom(hostId, ArtifactAccessLevels.HostOnly);

        ArtifactAccessHelper.HasAccessToRoomArtifacts(room, hostId).Should().BeTrue();
    }

    [Fact]
    public void Participant_IsRefused_OnAHostOnlyRoom()
    {
        var hostId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var room = CreateRoom(hostId, ArtifactAccessLevels.HostOnly, participantId);

        ArtifactAccessHelper.HasAccessToRoomArtifacts(room, participantId).Should().BeFalse();
    }

    [Fact]
    public void Participant_IsAdmitted_OnAnAllParticipantsRoom()
    {
        var hostId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var room = CreateRoom(hostId, ArtifactAccessLevels.AllParticipants, participantId);

        ArtifactAccessHelper.HasAccessToRoomArtifacts(room, participantId)
            .Should().BeTrue("ALL_PARTICIPANTS is the value the system actually persists");
    }

    [Fact]
    public void Stranger_IsRefused_EvenOnAnAllParticipantsRoom()
    {
        var hostId = Guid.NewGuid();
        var room = CreateRoom(hostId, ArtifactAccessLevels.AllParticipants, Guid.NewGuid());

        ArtifactAccessHelper.HasAccessToRoomArtifacts(room, Guid.NewGuid()).Should().BeFalse();
    }

    /// <summary>
    /// The exact strings the old guard compared against, plus near-misses. Any of them reaching the
    /// database means a room that denies everybody while reporting a permissive policy, so they
    /// must resolve to HOST_ONLY here — and the create/settings write paths reject them outright
    /// (see ArtifactAccessIntegrationTests) so they cannot get this far in the first place.
    /// </summary>
    [Theory]
    [InlineData("Participants")]
    [InlineData("Workspace")]
    [InlineData("HostOnly")]
    [InlineData("all_participants")]
    [InlineData("ALL PARTICIPANTS")]
    [InlineData("")]
    public void UnrecognisedLevel_FailsClosed(string level)
    {
        var hostId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var room = CreateRoom(hostId, level, participantId);

        ArtifactAccessHelper.HasAccessToRoomArtifacts(room, participantId).Should().BeFalse();
        ArtifactAccessHelper.HasAccessToRoomArtifacts(room, hostId).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json at all")]
    public void MalformedOrAbsentSettings_DenyNonHosts_RatherThanThrowing(string settingsJson)
    {
        var hostId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = Guid.NewGuid(),
            HostId = hostId,
            Settings = settingsJson,
            TranslationRoomParticipants = new List<TranslationRoomParticipant>
            {
                new() { UserId = participantId }
            }
        };

        // This used to escape the helper as an unhandled JsonException — a 500 out of an
        // authorization check, which is the wrong direction to fail in.
        ArtifactAccessHelper.HasAccessToRoomArtifacts(room, participantId).Should().BeFalse();
    }

    /// <summary>
    /// The overload the room-history projection uses, which resolves participation from a roster it
    /// already has rather than from the (unloaded) navigation. It must answer identically to the
    /// entity overload, or the list and the download endpoint drift apart again — which is exactly
    /// the defect this pair of overloads exists to prevent.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void RosterOverload_AgreesWithTheEntityOverload(bool isParticipant, bool expected)
    {
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var settingsJson = Serialize(ArtifactAccessLevels.AllParticipants);

        ArtifactAccessHelper.HasAccessToRoomArtifacts(hostId, settingsJson, isParticipant, userId)
            .Should().Be(expected);

        var room = isParticipant
            ? CreateRoom(hostId, ArtifactAccessLevels.AllParticipants, userId)
            : CreateRoom(hostId, ArtifactAccessLevels.AllParticipants);
        ArtifactAccessHelper.HasAccessToRoomArtifacts(room, userId).Should().Be(expected);
    }

    // WT-849: Invited-but-absent users should be treated like participants when the host
    // enables ALL_PARTICIPANTS. The old guard hard-blocked anyone with isParticipant == false,
    // which excluded every person who was invited but could not join — a majority of recipients
    // for enterprise meetings scheduled in advance.

    [Fact]
    public void Invitee_IsAdmitted_OnAnAllParticipantsRoom_WhenEmailProvided()
    {
        var hostId = Guid.NewGuid();
        var inviteeId = Guid.NewGuid();
        const string inviteeEmail = "alice@example.com";
        var room = CreateRoomWithInvitee(hostId, ArtifactAccessLevels.AllParticipants, inviteeEmail);

        // Not a participant (never joined), but was invited — should be admitted.
        ArtifactAccessHelper.HasAccessToRoomArtifacts(room, inviteeId, inviteeEmail)
            .Should().BeTrue("invited-but-absent members may see artifacts when host enables ALL_PARTICIPANTS");
    }

    [Fact]
    public void Invitee_IsRefused_OnAHostOnlyRoom_EvenWithEmail()
    {
        var hostId = Guid.NewGuid();
        var inviteeId = Guid.NewGuid();
        const string inviteeEmail = "bob@example.com";
        var room = CreateRoomWithInvitee(hostId, ArtifactAccessLevels.HostOnly, inviteeEmail);

        ArtifactAccessHelper.HasAccessToRoomArtifacts(room, inviteeId, inviteeEmail)
            .Should().BeFalse("HOST_ONLY still denies everyone who is not the host, even if invited");
    }

    [Fact]
    public void Invitee_EmailCaseInsensitive_IsAdmitted_OnAllParticipantsRoom()
    {
        // WT-496 established that email matching must be case-insensitive. WT-849 must not
        // reintroduce case-sensitivity through the invitation path.
        var hostId = Guid.NewGuid();
        var inviteeId = Guid.NewGuid();
        var room = CreateRoomWithInvitee(hostId, ArtifactAccessLevels.AllParticipants, "Alice@Example.COM");

        // Caller supplies the token-cased email; invitation is stored with a different case.
        ArtifactAccessHelper.HasAccessToRoomArtifacts(room, inviteeId, "alice@example.com")
            .Should().BeTrue("email comparison must be case-insensitive");
    }

    [Fact]
    public void Stranger_WithNoInvitation_IsRefused_OnAllParticipantsRoom()
    {
        var hostId = Guid.NewGuid();
        var room = CreateRoomWithInvitee(hostId, ArtifactAccessLevels.AllParticipants, "carol@example.com");

        // A completely different person — not a participant, not an invitee.
        ArtifactAccessHelper.HasAccessToRoomArtifacts(room, Guid.NewGuid(), "stranger@example.com")
            .Should().BeFalse("share-link strangers must still be denied");
    }

    [Fact]
    public void DescribeArtifactDenial_IsParticipantOrInvited_ReturnsHostSettingMessage()
    {
        ArtifactAccessHelper.DescribeArtifactDenial(isParticipantOrInvited: true)
            .Should().Contain("host");
    }

    [Fact]
    public void DescribeArtifactDenial_IsNeitherParticipantNorInvitee_ReturnsPresenceMessage()
    {
        ArtifactAccessHelper.DescribeArtifactDenial(isParticipantOrInvited: false)
            .Should().Contain("invited");
    }

    [Fact]
    public void TheVocabularyIsExactlyWhatTheWritersProduce()
    {
        // A new room's settings, straight from the value object's own default.
        new TranslationRoomSettings().ArtifactAccess.Should().Be(ArtifactAccessLevels.HostOnly);

        ArtifactAccessLevels.All.Should().Equal(
            ArtifactAccessLevels.HostOnly,
            ArtifactAccessLevels.AllParticipants);

        ArtifactAccessLevels.IsValid("HOST_ONLY").Should().BeTrue();
        ArtifactAccessLevels.IsValid("ALL_PARTICIPANTS").Should().BeTrue();
        ArtifactAccessLevels.IsValid("Participants").Should().BeFalse();
        ArtifactAccessLevels.IsValid("host_only").Should().BeFalse();
        ArtifactAccessLevels.IsValid(null).Should().BeFalse();
    }

    private static string Serialize(string artifactAccess)
        => JsonSerializer.Serialize(new TranslationRoomSettings { ArtifactAccess = artifactAccess });

    private static TranslationRoom CreateRoom(Guid hostId, string artifactAccess, params Guid[] participantIds)
    {
        var participants = new List<TranslationRoomParticipant>();
        foreach (var id in participantIds)
        {
            participants.Add(new TranslationRoomParticipant { UserId = id });
        }

        return new TranslationRoom
        {
            Id = Guid.NewGuid(),
            HostId = hostId,
            Settings = Serialize(artifactAccess),
            TranslationRoomParticipants = participants,
            TranslationRoomInvitations = new List<TranslationRoomInvitation>()
        };
    }

    /// <summary>
    /// Creates a room with NO joined participants, but with one email invitation in PENDING status.
    /// This is the WT-849 scenario: invited but absent.
    /// </summary>
    private static TranslationRoom CreateRoomWithInvitee(Guid hostId, string artifactAccess, string inviteeEmail)
    {
        return new TranslationRoom
        {
            Id = Guid.NewGuid(),
            HostId = hostId,
            Settings = Serialize(artifactAccess),
            TranslationRoomParticipants = new List<TranslationRoomParticipant>(),
            TranslationRoomInvitations = new List<TranslationRoomInvitation>
            {
                new()
                {
                    Email = inviteeEmail,
                    // PENDING is one of RoomReadAccess.InvitationStatusesGrantingRead.
                    // Storing the invitation but never joining leaves it PENDING.
                    Status = RoomReadAccess.InvitationStatusesGrantingRead[0]
                }
            }
        };
    }
}
