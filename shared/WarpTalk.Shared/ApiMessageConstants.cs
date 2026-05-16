namespace WarpTalk.Shared;

public static class ApiMessageConstants
{
    public static class ErrorMessages
    {
        // Common API ProblemDetails Titles & Details
        public const string ValidationFailedTitle = "Validation Failed";
        public const string UnauthorizedTokenDetail = "Could not extract a valid user ID from the authentication token.";
    }

    public static class ValidationMessages
    {
        public const string TitleRequired = "Title is required.";
        public const string TitleMaxLength = "Title cannot exceed 255 characters.";
    }
}
