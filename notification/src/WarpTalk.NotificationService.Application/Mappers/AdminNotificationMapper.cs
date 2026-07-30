using System;
using WarpTalk.NotificationService.Application.DTOs.AdminNotifications;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Application.Mappers;

public static class AdminNotificationMapper
{
    public static AdminNotification ToEntity(CreateAdminNotificationDto request, Guid adminId)
    {
        var targetAudienceData = new System.Collections.Generic.Dictionary<string, object>();
        if (request.SegmentId.HasValue) targetAudienceData["segmentId"] = request.SegmentId.Value;
        if (request.SpecificUserIds != null) targetAudienceData["userIds"] = request.SpecificUserIds;

        var payloadData = new System.Collections.Generic.Dictionary<string, object>();
        if (request.ImageUrl != null) payloadData["imageUrl"] = request.ImageUrl;
        if (request.CtaLink != null) payloadData["ctaLink"] = request.CtaLink;
        if (request.DiscountCode != null) payloadData["discountCode"] = request.DiscountCode;
        if (request.Severity != null) payloadData["severity"] = request.Severity;
        if (request.ActionRequired.HasValue) payloadData["actionRequired"] = request.ActionRequired.Value;
        if (request.DowntimeStart.HasValue) payloadData["downtimeStart"] = request.DowntimeStart.Value;
        if (request.DowntimeEnd.HasValue) payloadData["downtimeEnd"] = request.DowntimeEnd.Value;

        return new AdminNotification
        {
            Id = Guid.NewGuid(),
            Title = request.Title,
            Content = request.Content,
            Type = request.Type,
            TargetAudienceMode = request.TargetAudienceMode,
            TargetAudienceData = System.Text.Json.JsonSerializer.Serialize(targetAudienceData),
            Status = NotificationConstants.StatusPending,
            Payload = System.Text.Json.JsonSerializer.Serialize(payloadData),
            CreatedBy = adminId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public static AdminNotificationSummaryDto ToSummaryDto(AdminNotification entity)
    {
        return new AdminNotificationSummaryDto(
            entity.Id,
            entity.Title,
            entity.Type,
            entity.Status,
            entity.TargetAudienceMode,
            entity.CreatedBy,
            entity.CreatedAt,
            entity.UpdatedAt
        );
    }

    public static AdminNotificationDetailDto ToDetailDto(AdminNotification entity)
    {
        return new AdminNotificationDetailDto(
            entity.Id,
            entity.Title,
            entity.Content,
            entity.Type,
            entity.Status,
            entity.TargetAudienceMode,
            entity.TargetAudienceData,
            entity.Payload,
            entity.CreatedBy,
            entity.CreatedAt,
            entity.UpdatedAt
        );
    }
}
