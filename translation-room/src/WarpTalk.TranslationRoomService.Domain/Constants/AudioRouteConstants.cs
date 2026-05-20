namespace WarpTalk.TranslationRoomService.Domain.Constants;

public static class AudioRouteConstants
{
    // Error Messages
    public const string ErrorRoomPolicyIncomplete = "Room language policy is incomplete. Cannot generate routes.";
    public const string ErrorNoParticipantsInRoom = "No participants found in the translation room. Cannot generate routes.";
    public const string ErrorGenerateAudioRoutesUnexpected = "An unexpected error occurred while generating audio routes.";
    public const string ErrorFetchAudioRoutesUnexpected = "An unexpected error occurred while fetching audio routes.";
    public const string ErrorRouteNotFound = "Route not found.";
    public const string ErrorRouteNotBelongToRoom = "Route does not belong to the specified room.";
    public const string ErrorCannotUpdateCompletedRoute = "Cannot update a COMPLETED route.";
    public const string ErrorUnexpected = "Unexpected error occurred.";
    public const string ErrorUnknownEventType = "Unknown event type.";
    public const string ErrorInternalProcessingEvent = "Internal error processing event.";
    public const string ErrorFailedToProcessTelemetry = "Failed to process telemetry";

    // Validation Messages
    public const string ValidationSelfRoutingNotAllowed = "Self-routing is not allowed.";
    public const string ValidationSourceLanguageRequired = "Source language is required for audio routing.";
    public const string ValidationTargetLanguageRequired = "Target language is required for audio routing.";
    public const string ValidationRoomIdRequired = "Translation room identifier is required.";
    public const string ValidationStreamIdRequiredForActive = "StreamId is required when setting route to AUDIO_ROUTING_ACTIVE.";
}
