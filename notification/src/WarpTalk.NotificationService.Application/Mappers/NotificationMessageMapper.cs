using WarpTalk.NotificationService.Application.DTOs;
using WarpTalk.NotificationService.Domain.Entities;

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
}
