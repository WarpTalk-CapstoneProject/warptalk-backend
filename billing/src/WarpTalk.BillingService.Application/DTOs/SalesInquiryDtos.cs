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

public record SalesInquiryQuery(
    int Page = 1,
    int PageSize = 20,
    string? Status = null,
    string? Search = null,
    Guid? WorkspaceId = null);

public record UpdateSalesInquiryStatusRequest(
    [Required] string Status);

public record LinkSalesInquiryWorkspaceRequest(
    Guid WorkspaceId);

public record ConvertSalesInquiryToContractRequest(
    Guid WorkspaceId,
    Guid? PlanId,
    UpdateSubscriptionContractTermsRequest ContractTerms);
