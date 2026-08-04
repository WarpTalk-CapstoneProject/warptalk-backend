using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json;
using System;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared.Models;

namespace WarpTalk.BillingService.Application.Mappers;

public static class SalesInquiryMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static SalesInquiry ToEntity(this CreateSalesInquiryRequest request)
    {
        var now = DateTime.UtcNow;
        return new SalesInquiry
        {
            Id = Guid.NewGuid(),
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            WorkEmail = request.WorkEmail.Trim().ToLowerInvariant(),
            Company = request.Company.Trim(),
            RequestType = request.RequestType.Trim(),
            FeatureInterests = JsonSerializer.Serialize(request.FeatureInterests, JsonOptions),
            TargetLanguages = JsonSerializer.Serialize(request.TargetLanguages, JsonOptions),
            CurrentMonthlyMeetingVolume = request.CurrentMonthlyMeetingVolume.Trim(),
            ExpectedMonthlyMeetingVolumeInSixMonths = string.IsNullOrWhiteSpace(request.ExpectedMonthlyMeetingVolumeInSixMonths)
                ? null
                : request.ExpectedMonthlyMeetingVolumeInSixMonths.Trim(),
            UseCaseNotes = string.IsNullOrWhiteSpace(request.UseCaseNotes) ? null : request.UseCaseNotes.Trim(),
            PricingEstimateJson = request.PricingEstimate is null
                ? SalesInquiryConstants.JsonDefaults.EmptyObject
                : JsonSerializer.Serialize(request.PricingEstimate, JsonOptions),
            Consent = request.Consent,
            Source = string.IsNullOrWhiteSpace(request.Source) ? SalesInquiryConstants.Sources.LandingPricing : request.Source.Trim(),
            Status = SalesInquiryConstants.Statuses.New,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static SalesInquiryDto ToDto(this SalesInquiry inquiry)
        => new(
            inquiry.Id,
            inquiry.FirstName,
            inquiry.LastName,
            inquiry.WorkEmail,
            inquiry.Company,
            inquiry.RequestType,
            DeserializeList(inquiry.FeatureInterests),
            DeserializeList(inquiry.TargetLanguages),
            inquiry.CurrentMonthlyMeetingVolume,
            inquiry.ExpectedMonthlyMeetingVolumeInSixMonths,
            inquiry.UseCaseNotes,
            DeserializeObject(inquiry.PricingEstimateJson),
            inquiry.Consent,
            inquiry.Source,
            inquiry.Status,
            inquiry.WorkspaceId,
            inquiry.SubscriptionId,
            inquiry.CreatedAt,
            inquiry.UpdatedAt,
            inquiry.ConvertedAt,
            inquiry.ClosedAt);

    public static SalesInquiry ToWorkspaceEntity(this CreateWorkspaceSalesInquiryRequest request)
    {
        var inquiry = request.ToCreateRequest().ToEntity();
        inquiry.WorkspaceId = request.WorkspaceId;
        return inquiry;
    }

    public static UpdateSubscriptionContractTermsRequest ToContractTermsWithBillingContact(
        this ConvertSalesInquiryToContractRequest request,
        SalesInquiry inquiry)
    {
        return request.ContractTerms with
        {
            BillingContactEmail = string.IsNullOrWhiteSpace(request.ContractTerms.BillingContactEmail)
                ? inquiry.WorkEmail
                : request.ContractTerms.BillingContactEmail.Trim().ToLowerInvariant()
        };
    }

    public static CreateWorkspaceContractSubscriptionRequest ToContractSubscriptionRequest(
        this ConvertSalesInquiryToContractRequest request,
        Guid planId,
        UpdateSubscriptionContractTermsRequest terms)
        => new(request.WorkspaceId, planId, terms);

    private static IReadOnlyList<string> DeserializeList(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static object? DeserializeObject(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
