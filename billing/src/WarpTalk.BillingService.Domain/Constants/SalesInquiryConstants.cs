namespace WarpTalk.BillingService.Domain.Constants;

public static class SalesInquiryConstants
{
    public static class Defaults
    {
        public const int MaxPageSize = 100;
        public const int DuplicateWindowMinutes = 30;
        public const int MaxRequestedMonthlyCredits = 10_000_000;
        public const int MaxRequestedWorkspaceMembers = 10_000;
    }

    public static class JsonDefaults
    {
        public const string EmptyArray = "[]";
        public const string EmptyObject = "{}";
    }

    public static class Statuses
    {
        public const string New = "new";
        public const string Reviewing = "reviewing";
        public const string Quoted = "quoted";
        public const string Converted = "converted";
        public const string Closed = "closed";

        public static readonly ISet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            New,
            Reviewing,
            Quoted,
            Converted,
            Closed
        };
    }

    public static class Sources
    {
        public const string LandingPricing = "landing_pricing";
    }

    /// <summary>Sort keys the platform lead inbox accepts.</summary>
    public static class Sorts
    {
        public const string CreatedDesc = "created_desc";
        public const string CreatedAsc = "created_asc";
        public const string CompanyAsc = "company_asc";
        public const string CompanyDesc = "company_desc";

        public static readonly string[] All = [CreatedDesc, CreatedAsc, CompanyAsc, CompanyDesc];
    }

    public static class Errors
    {
        public const string ConsentRequired = "Consent is required before submitting a pricing inquiry.";
        public const string FeatureInterestRequired = "At least one feature interest is required.";
        public const string TargetLanguageRequired = "At least one target language is required.";
        public const string RequiredFieldsMissing = "Required pricing inquiry fields are missing.";
        public const string RequestedMonthlyCreditsInvalid = "Requested monthly credits must be between 1 and 10000000.";
        public const string RequestedWorkspaceMembersInvalid = "Requested workspace members must be between 1 and 10000.";
        public const string StatusInvalid = "Sales inquiry status is invalid.";
        public const string SortInvalid =
            "Unknown sort. Expected one of: created_desc, created_asc, company_asc, company_desc.";
        public const string NotFound = "Sales inquiry was not found.";
        public const string WorkspaceIdRequired = "WorkspaceId is required.";
        public const string EnterprisePlanNotFound = "Enterprise plan template was not found.";
    }

    public static class Messages
    {
        public const string ConvertedToNewContract = "Converted to new Enterprise Contract";
    }
}
