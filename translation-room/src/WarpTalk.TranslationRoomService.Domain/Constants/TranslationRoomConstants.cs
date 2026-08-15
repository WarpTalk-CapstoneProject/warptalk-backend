namespace WarpTalk.TranslationRoomService.Domain.Constants;

public static class TranslationRoomConstants
{
    // Terminal Statuses
    public static readonly string[] TerminalStatuses = new[]
    {
        "ENDED",
        "CANCELLED",
        "EXPIRED"
    };


    /// <summary>
    /// WT-281: what the auto-added host participant is called when the Auth directory cannot
    /// resolve the host's real name. This used to be the unconditional value, which is why
    /// production rosters showed a participant literally named "Host"; it is now only a degraded
    /// fallback, never the normal case.
    /// </summary>
    public const string HostDisplayNameFallback = "Host";

    /// <summary>
    /// The user id carried by the pseudo-participant that stands in for everyone on the far side
    /// of an EXTERNAL_BRIDGE room.
    ///
    /// It has to be a real, stable, non-null value rather than null, even though the column
    /// permits null: TranslationRoomAudioRouteMapper.ToDto publishes SourceUserId/TargetUserId to
    /// the AI workers, and tts_worker matches its speaker_id against those and not against the
    /// participant ids. A null here would leave the inbound route unmatchable and the far side
    /// silently untranslated.
    ///
    /// No row has to exist for it anywhere. translation_room_participants.user_id carries no
    /// foreign key to auth.users — the only FK on that table is translation_room_id — because the
    /// two live in different services. Nothing resolves this id through the Auth directory either,
    /// since the display name below is written directly.
    /// </summary>
    public static readonly Guid ExternalBridgeParticipantUserId =
        new("00000000-0000-0000-0000-00000000b21d");

    /// <summary>What the roster and the transcript call the far side of an external call.</summary>
    public const string ExternalBridgeDisplayName = "External Meeting";

    // Error Messages
    public const string ErrorRoomNotFound = "TranslationRoom not found";
    public const string ErrorRoomNotActive = "TranslationRoom not active or found";
    public const string ErrorRoomNotScheduled = "This room has no scheduled time to export.";
    public const string ErrorUnauthorizedEndRoom = "Unauthorized. Only host can end translationRoom.";
    public const string ErrorUnauthorizedUpdateRoom = "Unauthorized. Only host can update room settings.";
    public const string ErrorSettingsLocked = "Room settings cannot be updated after the room has entered IN_PROGRESS status.";

    // Lifecycle Transition Errors
    public const string ErrorInvalidTransitionToWaiting = "Room must be SCHEDULED to open waiting room.";
    public const string ErrorInvalidTransitionToInProgress = "Room must be WAITING or PAUSED to start or resume.";
    public const string ErrorInvalidTransitionToStart = "Only scheduled or waiting rooms can be started.";
    public const string ErrorInvalidTransitionToPaused = "Room must be IN_PROGRESS to pause.";
    public const string ErrorInvalidTransitionToEnded = "Room must be IN_PROGRESS or PAUSED to end.";
    public const string ErrorInvalidTransitionToCancelled = "Room must be SCHEDULED or WAITING to cancel.";
    public const string ErrorInvalidTransitionToExpired = "Room must be SCHEDULED or WAITING to expire.";
    public const string ErrorNoAudioRoutesConfigured = "The room needs at least one source/target audio route configured before it can start.";

    public const string ErrorFailedToCreateRoomTitle = "Failed to create room";
    public const string ErrorFailedToJoinRoomTitle = "Failed to join translation room";
    public const string ErrorFailedToEndRoomTitle = "Failed to end translation room";
    public const string ErrorParticipantKicked = "You have been permanently removed from this room and cannot rejoin.";

    /// <summary>WT-262. Format arg {0} is the room's MaxParticipants.</summary>
    public const string ErrorRoomAtCapacity = "This room is full ({0} participants). Ask the host to remove someone or start a larger room.";

    // Participant Errors
    public const string ErrorOnlyHostCanManageAudio = "Only the host can manage participant audio.";

    /// <summary>
    /// WT-313. Listing participants is a READ, so it must not borrow
    /// <see cref="ErrorUnauthorizedUpdateRoom"/> ("Only host can update room settings") — that message
    /// sent the WT-313 reporter hunting through the room-settings code for a bug that was in the
    /// waiting-room list. Keep this wording in sync with <see cref="ErrorUnauthorizedAdmitParticipant"/>
    /// and with the predicate they share.
    /// </summary>
    public const string ErrorUnauthorizedViewParticipants = "Unauthorized. Only the room host, a participant of this room, or a workspace owner/admin can view the participant list.";

    /// <summary>WT-188. Admission is "room host OR workspace Owner/Admin", never host-only.</summary>
    public const string ErrorUnauthorizedAdmitParticipant = "Only the host or a workspace owner/admin can admit participants.";

    public const string ErrorParticipantNotFound = "Participant not found.";
    public const string ErrorUnexpectedUpdateParticipantAudio = "An unexpected error occurred while updating participant audio.";
    public const string ErrorOnlyHostCanKick = "Only the host can kick participants.";
    public const string ErrorCannotKickHost = "Cannot kick the host.";
    public const string ErrorUnexpectedKickParticipant = "An unexpected error occurred while kicking participant.";
    public const string ErrorUnexpectedLeaveRoom = "An unexpected error occurred while leaving room.";

    // Artifact Errors
    public const string ErrorArtifactNotFound = "Artifact not found.";
    public const string ErrorUnauthorizedConsentArtifact = "Unauthorized to approve consent for this artifact.";

    // Unexpected General Errors
    public const string ErrorUnexpected = "An unexpected error occurred.";
    public const string ErrorUnexpectedEndRoom = "An unexpected error occurred while ending the room.";
    public const string ErrorUnexpectedUpdateRoomSettings = "An unexpected error occurred while updating the room settings.";

    // gRPC
    public const string EntityTranslationRoom = "TranslationRoom";

    // Validation Messages
    public const string ValidationSettingsRequired = "Room settings are required.";
    public const string ValidationSourceLanguageRequired = "Source language is required.";
    public const string ValidationTargetLanguagesRequired = "Target languages are required.";
    public const string ValidationMaxParticipantsGreaterThanZero = "Max participants must be strictly greater than 0.";
    public const string ValidationRoomTypeUnsupported = "Unsupported meeting type.";
    public const string ValidationScheduledTimeMustBeFuture = "Scheduled time must be strictly greater than the current time.";
    public const string ValidationTranslationRoomCodeRequired = "Translation room code is required.";
    public const string ValidationTranslationRoomCodeLength = "Translation room code must be exactly 12 characters.";
    public const string ValidationLanguageUnsupported = "Language '{0}' is not supported by the platform.";
    public const string ValidationArtifactAccessUnsupported = "Artifact access level '{0}' is not supported. Allowed values: {1}.";
    public const string ValidationLanguageNotAllowedByPolicy = "{0} language '{1}' is not allowed by room policy. It must be the source language or one of the target languages.";
    public const string ValidationSourceLanguageUnsupported = "Source language is not supported.";
    public const string ValidationListenLanguageRequired = "Listen language is required.";
    public const string ValidationSpeakLanguageRequired = "Speak language is required.";
    public const string ValidationDisplayNameRequired = "Display name is required.";
    public const string ValidationDisplayNameMaxLength = "Display name cannot exceed 100 characters.";
    public const string ValidationTranslationRoomCodeFormat = "Translation room code format is invalid.";
    public const string ValidationSearchTermMaxLength = "Search term cannot exceed 100 characters.";
    public const string ValidationInvalidParticipantStatus = "Status must be a valid TranslationRoomParticipantStatus.";
    public const string ValidationInvalidParticipantRole = "Role must be a valid TranslationRoomParticipantRole.";
    public const string ValidationInvalidSortBy = "SortBy must be one of: displayname, status, role, joinedat.";
}
