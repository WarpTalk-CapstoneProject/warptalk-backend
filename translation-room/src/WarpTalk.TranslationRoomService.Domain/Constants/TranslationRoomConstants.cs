namespace WarpTalk.TranslationRoomService.Domain.Constants;

public static class TranslationRoomConstants
{

    // Error Messages
    public const string ErrorRoomNotFound = "TranslationRoom not found";
    public const string ErrorRoomNotActive = "TranslationRoom not active or found";
    public const string ErrorUnauthorizedEndRoom = "Unauthorized. Only host can end translationRoom.";
        
    public const string ErrorFailedToCreateRoomTitle = "Failed to create room";
    public const string ErrorFailedToJoinRoomTitle = "Failed to join translation room";
    public const string ErrorFailedToEndRoomTitle = "Failed to end translation room";

    // gRPC
    public const string EntityTranslationRoom = "TranslationRoom";

    // Validation Messages
    public const string ValidationSourceLanguageRequired = "Source language is required.";
    public const string ValidationTargetLanguagesRequired = "Target languages are required.";
    public const string ValidationMaxParticipantsGreaterThanZero = "Max participants must be strictly greater than 0.";
    public const string ValidationScheduledTimeMustBeFuture = "Scheduled time must be strictly greater than the current time.";
}
