using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Domain.Entities;

public class SalesInquiry
{
    public Guid Id { get; set; }

    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    public string WorkEmail { get; set; } = string.Empty;

    public string Company { get; set; } = string.Empty;

    public string RequestType { get; set; } = string.Empty;

    public string FeatureInterests { get; set; } = "[]";

    public string TargetLanguages { get; set; } = "[]";

    public string CurrentMonthlyMeetingVolume { get; set; } = string.Empty;

    public string? ExpectedMonthlyMeetingVolumeInSixMonths { get; set; }

    public string? UseCaseNotes { get; set; }

    public string PricingEstimateJson { get; set; } = "{}";

    public bool Consent { get; set; }

    public string Source { get; set; } = SalesInquiryConstants.Sources.LandingPricing;

    public string Status { get; set; } = SalesInquiryConstants.Statuses.New;

    public Guid? WorkspaceId { get; set; }

    public Guid? SubscriptionId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? ConvertedAt { get; set; }

    public DateTime? ClosedAt { get; set; }

}
