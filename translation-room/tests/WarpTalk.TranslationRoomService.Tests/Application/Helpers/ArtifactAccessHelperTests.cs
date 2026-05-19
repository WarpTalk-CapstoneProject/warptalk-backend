using System;
using System.Collections.Generic;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Entities;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

public class ArtifactAccessHelperTests
{
    private TranslationRoom CreateRoom(Guid hostId, string settingsJson, params Guid[] participantIds)
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
            Settings = settingsJson,
            TranslationRoomParticipants = participants
        };
    }

    [Fact]
    public void HasAccessToRoomArtifacts_ShouldReturnTrue_WhenUserIsHost()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        var userId = hostId; // User is the host
        var room = CreateRoom(hostId, "{\"requires_approval\":true,\"artifact_access\":\"HostOnly\"}");

        // Act
        var result = ArtifactAccessHelper.HasAccessToRoomArtifacts(room, userId);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void HasAccessToRoomArtifacts_ShouldReturnFalse_WhenUserIsParticipant_AndAccessIsHostOnly()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var room = CreateRoom(hostId, "{\"requires_approval\":true,\"artifact_access\":\"HostOnly\"}", participantId);

        // Act
        var result = ArtifactAccessHelper.HasAccessToRoomArtifacts(room, participantId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void HasAccessToRoomArtifacts_ShouldReturnTrue_WhenUserIsParticipant_AndAccessIsParticipants()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var room = CreateRoom(hostId, "{\"requires_approval\":true,\"artifact_access\":\"Participants\"}", participantId);

        // Act
        var result = ArtifactAccessHelper.HasAccessToRoomArtifacts(room, participantId);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void HasAccessToRoomArtifacts_ShouldReturnTrue_WhenUserIsParticipant_AndAccessIsWorkspace()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var room = CreateRoom(hostId, "{\"requires_approval\":true,\"artifact_access\":\"Workspace\"}", participantId);

        // Act
        var result = ArtifactAccessHelper.HasAccessToRoomArtifacts(room, participantId);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void HasAccessToRoomArtifacts_ShouldReturnFalse_WhenUserIsNeitherHostNorParticipant()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var room = CreateRoom(hostId, "{\"requires_approval\":true,\"artifact_access\":\"Workspace\"}", participantId);

        // Act
        var result = ArtifactAccessHelper.HasAccessToRoomArtifacts(room, otherUserId);

        // Assert
        result.Should().BeFalse();
    }
}
