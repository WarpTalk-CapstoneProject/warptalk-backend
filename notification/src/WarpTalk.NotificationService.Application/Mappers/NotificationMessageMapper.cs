using WarpTalk.NotificationService.Application.DTOs;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.Shared.Protos;
using System.Text.Json;

namespace WarpTalk.NotificationService.Application.Mappers;

public static class NotificationMessageMapper
{
    public static NotificationMessage ToEntity(Guid userId, string type, string title, string content, string? actionUrl, string payloadJson)
    {
        return new NotificationMessage
        {
            UserId = userId,
            Type = type,
            Title = title,
            Content = content,
            ActionUrl = actionUrl,
            PayloadJson = payloadJson,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };
    }

    public static NotificationMessage ToEntity(CreateNotificationMessageDto dto)
    {
        return ToEntity(dto.UserId, dto.Type, dto.Title, dto.Content, dto.ActionUrl, dto.PayloadJson);
    }

    public static NotificationMessage ToEntity(AdminNotification adminNotif, Guid userId)
    {
        return new NotificationMessage
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = adminNotif.Title,
            Content = adminNotif.Content,
            Type = adminNotif.Type,
            ActionUrl = null,
            IsRead = false,
            CreatedAt = DateTime.UtcNow,
            PayloadJson = JsonSerializer.Serialize(new { AdminNotificationId = adminNotif.Id })
        };
    }

    public static CreateNotificationMessageDto ToCreateDto(SendNotificationRequest request, Guid userId, string payloadJson)
    {
        return new CreateNotificationMessageDto(
            userId,
            request.Type,
            request.Title,
            request.Body,
            string.IsNullOrEmpty(request.ActionUrl) ? null : request.ActionUrl,
            payloadJson
        );
    }

    public static NotificationMessageDto ToDto(NotificationMessage n)
    {
        return new NotificationMessageDto(
            n.Id, 
            n.Type, 
            n.Title, 
            n.Content, 
            n.ActionUrl, 
            n.PayloadJson, 
            n.IsRead, 
            n.ReadAt, 
            n.CreatedAt
        );
    }

    public static WarpTalk.Shared.Models.RealtimeNotificationMessage ToRealtimeDto(NotificationMessage n)
    {
        return new WarpTalk.Shared.Models.RealtimeNotificationMessage
        {
            Id = n.Id.ToString(),
            UserId = n.UserId.ToString(),
            Title = n.Title,
            Content = n.Content,
            Type = n.Type,
            ActionUrl = n.ActionUrl,
            PayloadJson = n.PayloadJson,
            CreatedAt = n.CreatedAt.ToString("O")
        };
    }

    public static WarpTalk.Shared.Models.RealtimeNotificationMessage ToRealtimeDto(NotificationMessageDto dto, string userId)
    {
        return new WarpTalk.Shared.Models.RealtimeNotificationMessage
        {
            Id = dto.Id.ToString(),
            UserId = userId,
            Title = dto.Title,
            Content = dto.Content,
            Type = dto.Type,
            ActionUrl = dto.ActionUrl ?? string.Empty,
            PayloadJson = dto.PayloadJson,
            CreatedAt = dto.CreatedAt.ToString("O")
        };
    }
}
