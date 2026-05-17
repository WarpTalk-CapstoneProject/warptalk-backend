using System;
using System.Linq;
using System.Text.Json;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

public static class ArtifactAccessHelper
{
    public static bool HasAccessToRoomArtifacts(TranslationRoom room, Guid userId)
    {
        if (room.HostId == userId) return true;

        var settings = !string.IsNullOrEmpty(room.Settings) ? JsonSerializer.Deserialize<TranslationRoomSettings>(room.Settings) : null;
        var isParticipant = room.TranslationRoomParticipants.Any(p => p.UserId == userId);
        
        return isParticipant && (settings?.HistoryAccess == ArtifactAccessLevel.Participants || settings?.HistoryAccess == ArtifactAccessLevel.Workspace);
    }
}
