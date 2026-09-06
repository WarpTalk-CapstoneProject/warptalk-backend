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
            settings.BreakoutsEnabled,
            settings.ParticipantsCanStartTranslation,
            settings.SaveTranscript);
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
            SeriesId: room.SeriesId,
            ExternalProvider: room.ExternalProvider,
            ExternalMeetingUrl: room.ExternalMeetingUrl,
            ExternalCalendarEventId: room.ExternalCalendarEventId,
            ExternalCalendarEventUrl: room.ExternalCalendarEventUrl
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
            ExternalProvider = request.ExternalProvider,
            ExternalMeetingUrl = request.ExternalMeetingUrl,
            ExternalCalendarEventId = request.ExternalCalendarEventId,
            ExternalCalendarEventUrl = request.ExternalCalendarEventUrl,
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
    ///  - INVITED, not CONNECTED (WT-450). CONNECTED is the one status that holds a seat, and
    ///    this row is written when the room is CREATED — so seeding it CONNECTED made every room
    ///    claim an occupant before anyone had opened it, which blinded both abandoned-room
    ///    reapers permanently. The host is promoted to CONNECTED on real arrival by
    ///    TranslationRoomParticipantMapper.UpdateFrom. EXTERNAL_BRIDGE is exempt — see the
    ///    reasoning at the assignment itself.
    /// </summary>
    /// <param name="roomType">
    /// EXTERNAL_BRIDGE inverts the listen rule above. In every other room the host is one of
    /// several people and wants the room's target language; in a bridge room the host is the only
    /// human present and the other "participant" is the external call, so the host wants to hear
    /// their OWN language. Seeding targetLanguages[0] there would make the inbound route
    /// en -> en, which translates nothing and would leave the far side apparently mute.
    /// </param>
    public static TranslationRoomParticipant BuildHostParticipant(
        Guid roomId,
        Guid hostId,
        string hostDisplayName,
        string sourceLanguage,
        IReadOnlyList<string> targetLanguages,
        string? roomType = null)
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
            ListenLanguage = TranslationRoomTypes.IsExternalBridge(roomType)
                ? sourceLanguage
                : targetLanguages[0],
            Role = "HOST",
            // WT-450. INVITED, not CONNECTED: booking a meeting is not attending it.
            //
            // This row is written when the room is CREATED, before the host has opened anything.
            // Seeding it CONNECTED made every room in the system claim an occupant from the
            // moment it existed, and CONNECTED is the sole definition of "holds a seat"
            // (TranslationRoomParticipantStatuses.SeatHolding). Three things read that:
            //
            //   * ParticipantCount — an empty room reported 1.
            //   * IdleRoomMonitoringWorker — `hasConnectedParticipants` was true, so it skipped
            //     the room.
            //   * AbandonedRoomSweepWorker — `seatHolders > 0`, so AbandonedRoomPolicy answered
            //     Leave, every sweep, forever.
            //
            // Both reapers were therefore BLIND to any room whose host never opened a socket —
            // and a socket is the only thing that ever clears this row, since
            // MarkParticipantDisconnectedAsync runs off the hub's OnDisconnectedAsync. A host who
            // pressed "Join meeting" (which calls /start and writes IN_PROGRESS) and then closed
            // the tab before the hub connected left a room that was IN_PROGRESS, unoccupied, and
            // permanently unsweepable. That is the reported bug: "In Progress" on a meeting
            // nobody started, next to an empty artifact list.
            //
            // Safe because the host is promoted on real arrival, unconditionally:
            // TranslationRoomParticipantMapper.UpdateFrom's `if (isHost)` branch sets CONNECTED
            // whatever the row said before, and INVITED is already an established non-seat
            // status the join path knows. The capacity check exempts the host explicitly
            // (`!isHost`), so nothing about admission changes. Routing is unaffected: a solo room
            // legitimately has zero routes — StartTranslationRoomAsync says so — and routes are
            // regenerated at Start and incrementally per join (S7).
            //
            // EXTERNAL_BRIDGE is exempt, and stays CONNECTED. That room's entire mesh is the host
            // plus the far-side stand-in, and GenerateRoutesAsync builds from seat holders — a
            // bridge room seeded with one seat generates no routes at all (see
            // ExternalBridgeParticipantTests.BothSeatsShouldHoldTheirSeatSoTheMeshSeesThem, and
            // the matching note in CreateTranslationRoomAsync). Exempting it also costs nothing
            // here: the stand-in is itself seeded CONNECTED and never disconnects, so a bridge
            // room's seat count is non-zero regardless of what the host's row says. Making the
            // host INVITED there would buy no sweepability and risk the audio mesh.
            Status = TranslationRoomTypes.IsExternalBridge(roomType)
                ? TranslationRoomParticipantStatuses.Connected
                : TranslationRoomParticipantStatuses.Invited,
            ConnectionType = "WEBRTC",
            IsTranslationAudioEnabled = true,
            IsUsingVoiceClone = false,
            // Untouched by the INVITED branch above, and set on arrival by the join path. Left as
            // the creation time so nothing that reads it null-references; it is not evidence of
            // attendance while the status says INVITED.
            JoinedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>
    /// The second seat in an EXTERNAL_BRIDGE room: one stand-in for everyone on the far side of
    /// the Google Meet / Zoom / Teams call.
    ///
    /// Its languages are the mirror of the host's, which is the whole trick. The existing mesh in
    /// TranslationRoomAudioRouteService pairs source.SpeakLanguage with target.ListenLanguage, so
    /// a host of vi/vi against a far side of en/en yields exactly the two routes the bridge needs
    /// — vi to en outbound, en to vi inbound — and every worker downstream treats this like any
    /// other room.
    ///
    /// IsUsingVoiceClone is false and must stay false. The people on the far side never agreed to
    /// anything with WarpTalk, so their voices are not cloned; they are dubbed in a stock voice.
    /// VoiceCloneConsentGate already fails closed for a participant with no consent record, so
    /// this is belt and braces rather than the only guard.
    /// </summary>
    public static TranslationRoomParticipant BuildExternalBridgeParticipant(
        Guid roomId,
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
            UserId = TranslationRoomConstants.ExternalBridgeParticipantUserId,
            DisplayName = TranslationRoomConstants.ExternalBridgeDisplayName,
            // Mirror of the host: speaks what the far side speaks, hears what the far side hears.
            SpeakLanguage = targetLanguages[0],
            ListenLanguage = targetLanguages[0],
            Role = "PARTICIPANT",
            Status = TranslationRoomParticipantStatuses.Connected,
            ConnectionType = ExternalBridgeConnectionType,
            IsTranslationAudioEnabled = true,
            IsUsingVoiceClone = false,
            JoinedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>
    /// Marks the stand-in row so a roster, a billing sweep or a presence check can tell it from a
    /// real person. The column is a free varchar that has only ever held "WEBRTC".
    /// </summary>
    public const string ExternalBridgeConnectionType = "EXTERNAL_BRIDGE";

    /// <summary>
    /// The settings a new room starts with: the meeting type's profile, with any value the
    /// caller stated explicitly taking precedence. Every RoomSettingsRequest member is
    /// nullable precisely so "not sent" and "sent false" stay distinguishable here — otherwise
    /// a caller who never mentions muting could not be told apart from one who asked for it
    /// off, and the type could never seed anything.
    /// </summary>
    /// <remarks>
    /// WT-343: a workspace-wide <c>EnforceHostApprovalDefault</c> sat between these two layers for
    /// one release. Host approval is a per-meeting decision, made on the toggle in the create
    /// dialog, and a workspace default for it was a second place to set the same thing. The owner
    /// removed it rather than keep two, so the precedence is back to explicit-then-type.
    /// </remarks>
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
            // No meeting-type default: WT-371 wants host-only unless a host says otherwise, and
            // seeding it per type would put the looser stance back where nobody chose it.
            ParticipantsCanStartTranslation = requested?.ParticipantsCanStartTranslation ?? false,
            // WT-587: likewise no per-type default. A meeting type says how a room is RUN; whether
            // the organisation keeps a record of it is not something a room template should decide
            // quietly, and the one direction that loses data has to be asked for out loud.
            SaveTranscript = requested?.SaveTranscript ?? true,
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
            room.SeriesId,
            ExternalProvider: room.ExternalProvider,
            ExternalMeetingUrl: room.ExternalMeetingUrl,
            ExternalCalendarEventId: room.ExternalCalendarEventId,
            ExternalCalendarEventUrl: room.ExternalCalendarEventUrl
        );
    }
}
