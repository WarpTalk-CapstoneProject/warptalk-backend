using System.ComponentModel.DataAnnotations;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.DTOs;

public record SalesInquiryDto(
    Guid Id,
    string FirstName,
    string LastName,
    string WorkEmail,
    string Company,
    string RequestType,
    IReadOnlyList<string> FeatureInterests,
    IReadOnlyList<string> TargetLanguages,
    string CurrentMonthlyMeetingVolume,
    string? ExpectedMonthlyMeetingVolumeInSixMonths,
    string? UseCaseNotes,
    object? PricingEstimate,
    bool Consent,
    string Source,
    string Status,
    Guid? WorkspaceId,
    Guid? SubscriptionId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ConvertedAt,
    DateTime? ClosedAt);

public record CreateSalesInquiryRequest(
    [Required][MaxLength(80)] string FirstName,
    [Required][MaxLength(80)] string LastName,
    [Required][EmailAddress][MaxLength(255)] string WorkEmail,
    [Required][MaxLength(160)] string Company,
    [Required][MaxLength(80)] string RequestType,
    [Required] IReadOnlyList<string> FeatureInterests,
    [Required] IReadOnlyList<string> TargetLanguages,
    [Required][MaxLength(80)] string CurrentMonthlyMeetingVolume,
    [MaxLength(80)] string? ExpectedMonthlyMeetingVolumeInSixMonths,
    string? UseCaseNotes,
    object? PricingEstimate,
    bool Consent,
    [MaxLength(80)] string? Source = null);

public record CreateWorkspaceSalesInquiryRequest(
    Guid WorkspaceId,
    [Required][MaxLength(80)] string FirstName,
    [Required][MaxLength(80)] string LastName,
    [Required][EmailAddress][MaxLength(255)] string WorkEmail,
    [Required][MaxLength(160)] string Company,
    [Required][MaxLength(80)] string RequestType,
    [Required] IReadOnlyList<string> FeatureInterests,
    [Required] IReadOnlyList<string> TargetLanguages,
    [Required][MaxLength(80)] string CurrentMonthlyMeetingVolume,
    [MaxLength(80)] string? ExpectedMonthlyMeetingVolumeInSixMonths,
    string? UseCaseNotes,
    object? PricingEstimate,
    bool Consent,
    [MaxLength(80)] string? Source = null) : IWorkspaceScopedRequest
{
    public CreateSalesInquiryRequest ToCreateRequest()
        => new(
            FirstName,
            LastName,
            WorkEmail,
            Company,
            RequestType,
            FeatureInterests,
            TargetLanguages,
            CurrentMonthlyMeetingVolume,
            ExpectedMonthlyMeetingVolumeInSixMonths,
            UseCaseNotes,
            PricingEstimate,
            Consent,
            Source);
}

/// <param name="RequestType">Exact match on request_type, case-insensitive. Null for every type.</param>
/// <param name="Source">Exact match on source (e.g. landing_pricing), case-insensitive. Null for every source.</param>
/// <param name="CreatedFrom">Inclusive lower bound on created_at (UTC; offset-less values are read as UTC).</param>
/// <param name="CreatedTo">Exclusive upper bound on created_at (UTC). Must not be earlier than CreatedFrom.</param>
/// <param name="Sort">
/// created_desc | created_asc | company_asc | company_desc. Only the admin inbox sends it; when
/// null the ordering is decided by <paramref name="NewestFirst"/> exactly as before, so the
/// workspace view keeps its open-first grouping.
/// </param>
public record SalesInquiryQuery(
    int Page = 1,
    int PageSize = 20,
    string? Status = null,
    string? Search = null,
    Guid? WorkspaceId = null,
    // The workspace view groups open inquiries first; the platform lead inbox reads strictly
    // newest-first so a lead that arrived this morning is never below last month's "new" ones.
    bool NewestFirst = false,
    string? RequestType = null,
    string? Source = null,
    DateTime? CreatedFrom = null,
    DateTime? CreatedTo = null,
    string? Sort = null);

public record UpdateSalesInquiryStatusRequest(
    [Required] string Status);

public record LinkSalesInquiryWorkspaceRequest(
    Guid WorkspaceId);

public record ConvertSalesInquiryToContractRequest(
    Guid WorkspaceId,
    Guid? PlanId,
    UpdateSubscriptionContractTermsRequest ContractTerms);
