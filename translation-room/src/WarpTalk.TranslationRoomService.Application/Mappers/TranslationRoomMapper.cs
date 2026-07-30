using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;

namespace WarpTalk.TranslationRoomService.Application.Mappers;

public static class TranslationRoomMapper
{
    public static TranslationRoomDto ToResponseDto(this TranslationRoom room)
    {
        var defaultSettings = new RoomSettingsResponse(true, "HOST_ONLY");
        RoomSettingsResponse settings = defaultSettings;
        if (!string.IsNullOrEmpty(room.Settings))
        {
            try
            {
                settings = System.Text.Json.JsonSerializer.Deserialize<RoomSettingsResponse>(
                    room.Settings,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? defaultSettings;
            }
            catch { /* malformed JSON in DB — use default */ }
        }

        return new TranslationRoomDto(
            room.Id,
            room.WorkspaceId,
            room.HostId,
            room.Title,
            room.Description,
            room.TranslationRoomCode,
            Enum.TryParse<RoomStatus>(room.Status, true, out var parsedStatus) ? parsedStatus : RoomStatus.SCHEDULED,
            room.TranslationRoomType,
            room.MaxParticipants,
            room.SourceLanguage,
            Helpers.LanguageHelper.ParseTargetLanguages(room.TargetLanguages),
            room.ScheduledAt,
            null, // InvitedEmails
            room.StartedAt,
            room.EndedAt,
            room.DurationSeconds,
            room.CreatedAt,
            settings
        );
    }

    public static TranslationRoom ToEntity(this CreateTranslationRoomRequest request, Guid hostId, string roomCode, string status, string sourceLanguage, List<string> targetLanguages)
    {
        if (!request.WorkspaceId.HasValue || request.WorkspaceId.Value == Guid.Empty)
            throw new ArgumentException("WorkspaceId must be a valid workspace.", nameof(request));

        return new TranslationRoom
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = request.WorkspaceId.Value,
            HostId = hostId,
            Title = request.Title,
            Description = request.Description,
            TranslationRoomCode = roomCode,
            Status = status,
            TranslationRoomType = request.TranslationRoomType ?? "INSTANT",
            MaxParticipants = request.MaxParticipants,
            SourceLanguage = sourceLanguage,
            TargetLanguages = Helpers.LanguageHelper.SerializeTargetLanguages(targetLanguages),
            Settings = System.Text.Json.JsonSerializer.Serialize(
                request.Settings != null
                    ? new TranslationRoomSettings { RequiresApproval = request.Settings.RequiresApproval, ArtifactAccess = request.Settings.ArtifactAccess }
                    : new TranslationRoomSettings { RequiresApproval = true, ArtifactAccess = "HOST_ONLY" }),
            ScheduledAt = request.ScheduledAt,
            IsActive = true
        };
    }

    public static TranslationRoomDto ToHistoryDto(this TranslationRoom room)
    {
        var defaultSettings = new RoomSettingsResponse(true, "HOST_ONLY");
        RoomSettingsResponse settings = defaultSettings;
        if (!string.IsNullOrEmpty(room.Settings))
        {
            try
            {
                settings = System.Text.Json.JsonSerializer.Deserialize<RoomSettingsResponse>(
                    room.Settings,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? defaultSettings;
            }
            catch { /* malformed JSON in DB — use default */ }
        }

        var artifacts = room.TranslationRoomArtifacts?.Select(a => a.ToDto()).ToList() ?? new List<RoomArtifactDto>();

        return new TranslationRoomDto(
            room.Id,
            room.WorkspaceId,
            room.HostId,
            room.Title,
            room.Description,
            room.TranslationRoomCode,
            Enum.TryParse<RoomStatus>(room.Status, true, out var parsedStatus) ? parsedStatus : RoomStatus.SCHEDULED,
            room.TranslationRoomType,
            room.MaxParticipants,
            room.SourceLanguage,
            Helpers.LanguageHelper.ParseTargetLanguages(room.TargetLanguages),
            room.ScheduledAt,
            null, // InvitedEmails
            room.StartedAt,
            room.EndedAt,
            room.DurationSeconds,
            room.CreatedAt,
            settings,
            artifacts
        );
    }
}
