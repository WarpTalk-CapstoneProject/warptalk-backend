using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Domain.Constants;

public static class TranslationRoomConstants
{
    // Terminal Statuses
    public static readonly RoomStatus[] TerminalStatuses = new[] 
    { 
        RoomStatus.ENDED, 
        RoomStatus.CANCELLED, 
        RoomStatus.EXPIRED 
    };


    // Error Messages
    public const string ErrorRoomNotFound = "TranslationRoom not found";
    public const string ErrorRoomNotActive = "TranslationRoom not active or found";
    public const string ErrorUnauthorizedEndRoom = "Unauthorized. Only host can end translationRoom.";
    public const string ErrorUnauthorizedUpdateRoom = "Unauthorized. Only host can update room settings.";
    public const string ErrorSettingsLocked = "Room settings cannot be updated after the room has entered IN_PROGRESS status.";
    
    // Lifecycle Transition Errors
    public const string ErrorInvalidTransitionToWaiting = "Room must be SCHEDULED to open waiting room.";
    public const string ErrorInvalidTransitionToInProgress = "Room must be WAITING or PAUSED to start or resume.";
    public const string ErrorInvalidTransitionToPaused = "Room must be IN_PROGRESS to pause.";
    public const string ErrorInvalidTransitionToEnded = "Room must be IN_PROGRESS or PAUSED to end.";
    public const string ErrorInvalidTransitionToCancelled = "Room must be SCHEDULED or WAITING to cancel.";
    public const string ErrorInvalidTransitionToExpired = "Room must be SCHEDULED or WAITING to expire.";
        
    public const string ErrorFailedToCreateRoomTitle = "Failed to create room";
    public const string ErrorFailedToJoinRoomTitle = "Failed to join translation room";
    public const string ErrorFailedToEndRoomTitle = "Failed to end translation room";
    public const string ErrorParticipantKicked = "You have been permanently removed from this room and cannot rejoin.";

    // gRPC
    public const string EntityTranslationRoom = "TranslationRoom";

    // Validation Messages
    public const string ValidationSettingsRequired = "Room settings are required.";
    public const string ValidationSourceLanguageRequired = "Source language is required.";
    public const string ValidationTargetLanguagesRequired = "Target languages are required.";
    public const string ValidationMaxParticipantsGreaterThanZero = "Max participants must be strictly greater than 0.";
    public const string ValidationScheduledTimeMustBeFuture = "Scheduled time must be strictly greater than the current time.";
    public const string ValidationTranslationRoomCodeRequired = "Translation room code is required.";
    public const string ValidationTranslationRoomCodeLength = "Translation room code must be exactly 12 characters.";
    public const string ValidationLanguageUnsupported = "Language '{0}' is not supported by the platform.";
    public const string ValidationLanguageNotAllowedByPolicy = "{0} language '{1}' is not allowed by room policy. It must be the source language or one of the target languages.";
    public const string ValidationSourceLanguageUnsupported = "Source language is not supported.";
    public const string ValidationListenLanguageRequired = "Listen language is required.";
    public const string ValidationSpeakLanguageRequired = "Speak language is required.";
    public const string ValidationDisplayNameRequired = "Display name is required.";
    public const string ValidationDisplayNameMaxLength = "Display name cannot exceed 100 characters.";
    public const string ValidationTranslationRoomCodeFormat = "Translation room code format must be xxx-yyyy-zzz using only lowercase letters (e.g., abc-defg-hij).";
    public const string ValidationSearchTermMaxLength = "Search term cannot exceed 100 characters.";
    public const string ValidationInvalidParticipantStatus = "Status must be a valid TranslationRoomParticipantStatus.";
    public const string ValidationInvalidParticipantRole = "Role must be a valid TranslationRoomParticipantRole.";
    public const string ValidationInvalidSortBy = "SortBy must be one of: displayname, status, role, joinedat.";
}
