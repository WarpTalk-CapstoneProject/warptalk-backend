using System;
using System.Collections.Generic;

namespace WarpTalk.AssistantService.Application.DTOs;

public class AssistantConversationDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastMessageAt { get; set; }
    public bool IsArchived { get; set; }
}

public class AssistantConversationDetailDto : AssistantConversationDto
{
    public List<AssistantMessageDto> Messages { get; set; } = new();
}
