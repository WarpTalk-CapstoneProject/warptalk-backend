using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;

namespace WarpTalk.TranslationRoomService.Application.Mappers;

public static class TranslationRoomMapper
{
    /// <summary>
    /// Reads the room's settings blob into the response shape.
    ///
    /// Deserializes into the DOMAIN type, not straight into RoomSettingsResponse. The blob is
    /// written with snake_case names ([JsonPropertyName("requires_approval")] and friends),
    /// while the response record's members are PascalCase with no attributes — and
    /// PropertyNameCaseInsensitive does not bridge an underscore. Binding therefore never
    /// matched, so every room came back reporting requires_approval = false no matter what was
    /// stored. Only the API's view was wrong (the service reads TranslationRoomSettings
    /// directly, which is why the waiting room itself always behaved correctly), but the same
    /// silence would have swallowed mute_on_entry, auto_record and breakouts_enabled.
    /// </summary>
    public static RoomSettingsResponse ReadSettings(string? settingsJson)
    {
        var settings = new TranslationRoomSettings();

        if (!string.IsNullOrEmpty(settingsJson))
        {
            try
            {
                settings = System.Text.Json.JsonSerializer.Deserialize<TranslationRoomSettings>(
                    settingsJson,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new TranslationRoomSettings();
            }
            catch { /* malformed JSON in DB — use defaults */ }
        }

        return new RoomSettingsResponse(
            settings.RequiresApproval,
            settings.ArtifactAccess,
            settings.MuteOnEntry,
            settings.AutoRecord,
            settings.BreakoutsEnabled);
    }

    public static TranslationRoomDto ToResponseDto(this TranslationRoom room)
    {
        var settings = ReadSettings(room.Settings);

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
        // Unknown types are rejected by the validator; this normalization only folds spelling
        // ("Channel Meeting" → CHANNEL_MEETING) and defaults an omitted type to EVENT.
        var roomType = TranslationRoomTypes.Normalize(request.TranslationRoomType) ?? TranslationRoomTypes.Event;
        var defaults = TranslationRoomTypePolicy.For(roomType);

        return new TranslationRoom
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = request.WorkspaceId ?? Guid.Empty,
            HostId = hostId,
            Title = request.Title,
            Description = request.Description,
            TranslationRoomCode = roomCode,
            Status = status,
            TranslationRoomType = roomType,
            // The type sets the seat count unless the caller asked for a specific one — a
            // Virtual Appointment is 1:1 and a Live Event is not, and neither should silently
            // inherit whatever number a client hardcoded.
            MaxParticipants = request.MaxParticipants is > 0 ? request.MaxParticipants.Value : defaults.MaxParticipants,
            SourceLanguage = sourceLanguage,
            TargetLanguages = Helpers.LanguageHelper.SerializeTargetLanguages(targetLanguages),
            Settings = System.Text.Json.JsonSerializer.Serialize(ResolveSettings(roomType, request.Settings)),
            ScheduledAt = request.ScheduledAt,
            IsActive = true
        };
    }

    /// <summary>
    /// The settings a new room starts with: the meeting type's profile, with any value the
    /// caller stated explicitly taking precedence. Every RoomSettingsRequest member is
    /// nullable precisely so "not sent" and "sent false" stay distinguishable here — otherwise
    /// a caller who never mentions muting could not be told apart from one who asked for it
    /// off, and the type could never seed anything.
    /// </summary>
    public static TranslationRoomSettings ResolveSettings(string roomType, RoomSettingsRequest? requested)
    {
        var defaults = TranslationRoomTypePolicy.For(roomType);

        return new TranslationRoomSettings
        {
            RequiresApproval = requested?.RequiresApproval ?? defaults.RequiresApproval,
            ArtifactAccess = requested?.ArtifactAccess ?? "HOST_ONLY",
            MuteOnEntry = requested?.MuteOnEntry ?? defaults.MuteOnEntry,
            AutoRecord = requested?.AutoRecord ?? defaults.AutoRecord,
            BreakoutsEnabled = requested?.BreakoutsEnabled ?? defaults.BreakoutsEnabled,
        };
    }

    public static TranslationRoomDto ToHistoryDto(this TranslationRoom room)
    {
        var settings = ReadSettings(room.Settings);

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
