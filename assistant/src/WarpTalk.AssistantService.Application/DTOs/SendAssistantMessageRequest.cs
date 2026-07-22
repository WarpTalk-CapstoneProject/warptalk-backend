using System;
using System.ComponentModel.DataAnnotations;

namespace WarpTalk.AssistantService.Application.DTOs;

public record SendAssistantMessageRequest(
    [Required] string Content
);

public record SendAssistantMessageResponse(
    Guid MessageId,
    Guid AssistantMessageId
);

public record CreateAssistantConversationRequest(
    [Required] Guid WorkspaceId
);
