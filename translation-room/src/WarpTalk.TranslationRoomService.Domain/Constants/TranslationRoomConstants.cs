namespace WarpTalk.TranslationRoomService.Domain.Constants;

public static class TranslationRoomConstants
{
    public const int MaxTitleLength = 255;
    public const int TranslationRoomCodeLength = 12;

    public static class ErrorMessages
    {
        public const string RoomNotFound = "TranslationRoom not found";
        public const string RoomNotActive = "TranslationRoom not active or found";
        public const string UnauthorizedEndRoom = "Unauthorized. Only host can end translationRoom.";
        
        public const string FailedToCreateRoomTitle = "Failed to create room";
        public const string FailedToJoinRoomTitle = "Failed to join translation room";
        public const string FailedToEndRoomTitle = "Failed to end translation room";

        // gRPC
        public const string EntityTranslationRoom = "TranslationRoom";
    }

    public static class ValidationMessages
    {
        public const string TitleRequired = "Title is required.";
        public const string TitleMaxLength = "Title cannot exceed 255 characters.";
        public const string SourceLanguageRequired = "Source language is required.";
        public const string TargetLanguagesRequired = "Target languages are required.";
        public const string MaxParticipantsGreaterThanZero = "Max participants must be strictly greater than 0.";
        public const string ScheduledTimeMustBeFuture = "Scheduled time must be strictly greater than the current time.";
    }

    public static class Roles
    {
        public const string Participant = "participant";
        public const string Host = "host";
    }

    public static class ParticipantStatus
    {
        public const string Connected = "connected";
        public const string Disconnected = "disconnected";
    }
}
