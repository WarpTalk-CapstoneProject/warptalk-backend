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

    /// <summary>
    /// WT-280: <paramref name="participantCount"/> is how many participants currently hold a seat
    /// (CONNECTED only, per TranslationRoomParticipantStatuses.SeatHolding). It is a required
    /// argument rather than something read off <c>room.TranslationRoomParticipants</c>, because
    /// that navigation is not loaded on most paths and counting it silently yields 0.
    /// </summary>
    public static TranslationRoomDto ToResponseDto(this TranslationRoom room, int participantCount)
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
            settings,
            participantCount,
            // WT-327: no artifacts on this path; named so the series id below cannot be
            // mistaken for the artifact list.
            Artifacts: null,
            SeriesId: room.SeriesId
        );
    }

    public static TranslationRoom ToEntity(this CreateTranslationRoomRequest request, Guid hostId, string roomCode, string status, string sourceLanguage, List<string> targetLanguages)
    {
        if (!request.WorkspaceId.HasValue || request.WorkspaceId.Value == Guid.Empty)
            throw new ArgumentException("WorkspaceId must be a valid workspace.", nameof(request));
        // Unknown types are rejected by the validator; this normalization only folds spelling
        // ("Channel Meeting" → CHANNEL_MEETING) and defaults an omitted type to EVENT.
        var roomType = TranslationRoomTypes.Normalize(request.TranslationRoomType) ?? TranslationRoomTypes.Event;
        var defaults = TranslationRoomTypePolicy.For(roomType);

        return new TranslationRoom
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = request.WorkspaceId.Value,
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
    /// The host's own participant row for a freshly created room (WT-82 / WT-281).
    ///
    /// WT-327 lifted this out of TranslationRoomService because a recurring series materialises
    /// rooms on a second code path, and two hand-written copies of these rules would drift. The
    /// rules, unchanged:
    ///  - DisplayName is the host's resolved name, falling back to the role label only when the
    ///    Auth directory cannot answer (WT-281 — the fallback is not the normal case).
    ///  - The host SPEAKS the room's source language and LISTENS in <c>targetLanguages[0]</c>.
    ///    Seeding both from the source produced "English -> English" rooms, which is not a
    ///    translation at all. First target, deliberately: it is the language the room was
    ///    primarily opened for, the host's global DefaultListenLanguage need not be among this
    ///    room's targets, and null is not an option because listen_language is NOT NULL and the
    ///    audio-route pipeline keys off it. It is a seed, not a lock.
    ///  - CONNECTED, because that is the one status that holds a seat
    ///    (TranslationRoomParticipantStatuses.SeatHolding) and every write path in the service
    ///    stores it.
    /// </summary>
    public static TranslationRoomParticipant BuildHostParticipant(
        Guid roomId,
        Guid hostId,
        string hostDisplayName,
        string sourceLanguage,
        IReadOnlyList<string> targetLanguages)
    {
        if (targetLanguages is null || targetLanguages.Count == 0)
            throw new ArgumentException("A room always has at least one target language.", nameof(targetLanguages));

        var now = DateTime.UtcNow;

        return new TranslationRoomParticipant
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = roomId,
            UserId = hostId,
            DisplayName = hostDisplayName,
            SpeakLanguage = sourceLanguage,
            ListenLanguage = targetLanguages[0],
            Role = "HOST",
            Status = TranslationRoomParticipantStatuses.Connected,
            ConnectionType = "WEBRTC",
            IsTranslationAudioEnabled = true,
            IsUsingVoiceClone = false,
            JoinedAt = now,
            CreatedAt = now,
            UpdatedAt = now
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
            ArtifactAccess = requested?.ArtifactAccess ?? ArtifactAccessLevels.HostOnly,
            MuteOnEntry = requested?.MuteOnEntry ?? defaults.MuteOnEntry,
            AutoRecord = requested?.AutoRecord ?? defaults.AutoRecord,
            BreakoutsEnabled = requested?.BreakoutsEnabled ?? defaults.BreakoutsEnabled,
        };
    }

    /// <inheritdoc cref="ToResponseDto(TranslationRoom, int)" path="/summary"/>
    public static TranslationRoomDto ToHistoryDto(this TranslationRoom room, int participantCount)
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
            participantCount,
            artifacts,
            room.SeriesId
        );
    }
}
