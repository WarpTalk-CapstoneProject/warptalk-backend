using System.Linq;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Mappers;

public static class AssistantMapper
{
    public static AssistantConversationDto ToDto(this AssistantConversation entity)
    {
        return new AssistantConversationDto
        {
            Id = entity.Id,
            Title = entity.Title,
            CreatedAt = entity.CreatedAt,
            LastMessageAt = entity.LastMessageAt,
            IsArchived = entity.IsArchived,
        };
    }

    public static AssistantConversationDetailDto ToDetailDto(this AssistantConversation entity)
    {
        return new AssistantConversationDetailDto
        {
            Id = entity.Id,
            Title = entity.Title,
            CreatedAt = entity.CreatedAt,
            LastMessageAt = entity.LastMessageAt,
            IsArchived = entity.IsArchived,
            Messages = entity.Messages
                .OrderBy(m => m.CreatedAt)
                .Select(m => m.ToDto())
                .ToList(),
        };
    }

    public static AssistantMessageDto ToDto(this AssistantMessage entity)
    {
        return new AssistantMessageDto
        {
            Id = entity.Id,
            ConversationId = entity.ConversationId,
            Role = entity.Role,
            Content = entity.Content,
            Status = entity.Status,
            CreatedAt = entity.CreatedAt,
            CompletedAt = entity.CompletedAt,
            SourcesJson = entity.SourcesJson,
            MentionsJson = entity.MentionsJson,
        };
    }

    public static AssistantConversationDto ToDto(this PlatformConversation entity)
    {
        return new AssistantConversationDto
        {
            Id = entity.Id,
            Title = entity.Title,
            CreatedAt = entity.CreatedAt,
            LastMessageAt = entity.LastMessageAt,
            IsArchived = entity.IsArchived,
            Scope = AssistantConversationScopes.Platform,
        };
    }

    public static AssistantConversationDetailDto ToDetailDto(this PlatformConversation entity)
    {
        return new AssistantConversationDetailDto
        {
            Id = entity.Id,
            Title = entity.Title,
            CreatedAt = entity.CreatedAt,
            LastMessageAt = entity.LastMessageAt,
            IsArchived = entity.IsArchived,
            Scope = AssistantConversationScopes.Platform,
            Messages = entity.Messages
                .OrderBy(m => m.CreatedAt)
                .Select(m => m.ToDto())
                .ToList(),
        };
    }

    /// <summary>The same wire shape as a workspace message, so one client renders both.</summary>
    public static AssistantMessageDto ToDto(this PlatformMessage entity)
    {
        return new AssistantMessageDto
        {
            Id = entity.Id,
            ConversationId = entity.ConversationId,
            Role = entity.Role,
            Content = entity.Content,
            Status = entity.Status,
            CreatedAt = entity.CreatedAt,
            CompletedAt = entity.CompletedAt,
            SourcesJson = entity.SourcesJson,
        };
    }
}
