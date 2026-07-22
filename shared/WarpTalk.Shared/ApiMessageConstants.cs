namespace WarpTalk.Shared;

public static class ApiMessageConstants
{
    public static class ErrorMessages
    {
        // Common API ProblemDetails Titles & Details
        public const string ValidationFailedTitle = "Validation Failed";
        public const string UnauthorizedTokenDetail = "Could not extract a valid user ID from the authentication token.";

        // Billing
        public const string BillingInternalError = "An unexpected error occurred.";
        public const string BillingSubscriptionNotFound = "No active subscription found.";
        public const string BillingSubscriptionAlreadyActive = "Workspace already has an active subscription.";
        public const string BillingPlanNotFound = "Plan not found.";
        public const string BillingInvalidWorkspaceId = "Workspace ID cannot be empty.";
        public const string BillingInvalidAmount = "Amount must be greater than 0.";
        public const string BillingInsufficientCredits = "Insufficient credits.";
        public const string BillingWorkspaceNotFound = "Workspace not found.";
        public const string BillingConcurrencyConflict = "A conflict occurred. Please retry.";
        public const string BillingAccessDenied = "Access denied.";
        public const string BillingValidationFailed = "Validation failed.";
    }

    public static class ValidationMessages
    {
        public const string TitleRequired = "Title is required.";
        public const string TitleMaxLength = "Title cannot exceed 255 characters.";

        // Auth & Profiles
        public const string EmailRequired = "Email is required.";
        public const string EmailInvalidFormat = "Email must be a valid @gmail.com address.";
        public const string PasswordRequired = "Password is required.";
        public const string PasswordMinLength = "Password must be at least 6 characters long.";
        public const string FullNameRequired = "Full name is required.";
        public const string FullNameNotEmpty = "Full name cannot be empty.";
        public const string RefreshTokenRequired = "Refresh token is required.";
        public const string GoogleIdTokenRequired = "Google ID token is required.";
        public const string PreferredLanguageInvalid = "Preferred language format is invalid.";
        public const string TimezoneInvalid = "Mã IANA timezone không hợp lệ.";
        public const string NewPasswordRequired = "New password is required.";
        public const string NewPasswordMinLength = "New password must be at least 6 characters long.";

        // User Settings
        public const string FontSizeOutOfBounds = "Font size must be between {0} and {1}.";
        public const string MaxParticipantsOutOfBounds = "Default max participants must be between {0} and {1}.";
        public const string InvalidTheme = "Invalid theme. Supported: {0}, {1}, {2}.";
        public const string InvalidRoomType = "Invalid translation room type.";
        public const string InvalidSpeakLanguage = "Invalid default speak language format.";
        public const string InvalidListenLanguage = "Invalid default listen language format.";

        // Billing
        public const string AmountGreaterThanZero = "Amount must be greater than 0.";
        public const string PlanIdRequired = "Plan ID is required.";
        public const string ReferenceTypeRequired = "ReferenceType is required.";
        public const string PageSizeOutOfBounds = "Page size must be between 1 and 200.";
        public const string PageNumberOutOfBounds = "Page number must be >= 1.";

    }
}
