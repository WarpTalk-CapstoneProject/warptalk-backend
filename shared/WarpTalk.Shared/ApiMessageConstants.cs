namespace WarpTalk.Shared;

public static class ApiMessageConstants
{
    public static class ErrorMessages
    {
        // Common API ProblemDetails Titles & Details
        public const string ValidationFailedTitle = "Validation Failed";
        public const string UnauthorizedTokenDetail = "Could not extract a valid user ID from the authentication token.";
    }
}
