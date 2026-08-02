using FluentAssertions;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.Application.Helpers;

public class SalesInquiryHelperTests
{
    [Fact]
    public void ValidateCreate_Should_Accept_Enterprise_Request_Within_Limits()
    {
        var request = CreateRequest(new
        {
            requestedMonthlyCredits = "10000000",
            requestedWorkspaceMembers = "10000"
        });

        var result = SalesInquiryHelper.ValidateCreate(request);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ValidateCreate_Should_Reject_Requested_Monthly_Credits_Above_Limit()
    {
        var request = CreateRequest(new
        {
            requestedMonthlyCredits = "10000001",
            requestedWorkspaceMembers = "100"
        });

        var result = SalesInquiryHelper.ValidateCreate(request);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(SalesInquiryConstants.Errors.RequestedMonthlyCreditsInvalid);
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }

    [Fact]
    public void ValidateCreate_Should_Reject_Requested_Workspace_Members_Above_Limit()
    {
        var request = CreateRequest(new
        {
            requestedMonthlyCredits = "700000",
            requestedWorkspaceMembers = "10001"
        });

        var result = SalesInquiryHelper.ValidateCreate(request);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(SalesInquiryConstants.Errors.RequestedWorkspaceMembersInvalid);
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }

    private static CreateSalesInquiryRequest CreateRequest(object pricingEstimate)
        => new(
            FirstName: "Demo",
            LastName: "User",
            WorkEmail: "demo@enterprise.vn",
            Company: "FPT-SEP490-SU26",
            RequestType: "enterprise_contract_request",
            FeatureInterests: new[] { "enterprise_contract" },
            TargetLanguages: new[] { "en", "vi" },
            CurrentMonthlyMeetingVolume: "700000 credits / month",
            ExpectedMonthlyMeetingVolumeInSixMonths: "700000 credits / month",
            UseCaseNotes: null,
            PricingEstimate: pricingEstimate,
            Consent: true,
            Source: "workspace_billing_trial");
}
