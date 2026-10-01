using System.ComponentModel.DataAnnotations;

namespace WarpTalk.AssistantService.Application.DTOs;

/// <summary>
/// Which conversation store a WarpBot conversation lives in. Sent on every conversation DTO so a
/// client can never render a platform thread as if it belonged to the workspace it has open.
/// </summary>
public static class AssistantConversationScopes
{
    /// <summary>A workspace member's conversation, scoped to one workspace. Every conversation until now.</summary>
    public const string Workspace = "workspace";

    /// <summary>A system administrator's conversation in the admin portal, about the whole platform.</summary>
    public const string Platform = "platform";
}

/// <summary>Starts a platform-scope conversation. There is deliberately no workspace id to send.</summary>
public record CreatePlatformConversationRequest(string? Title = null);

/// <summary>
/// One platform-scope turn. Text only: no page context, @mentions, attachments or plugin switches,
/// because every one of those names workspace content, and a platform turn is answered only from
/// the read-only admin APIs.
/// </summary>
public record SendPlatformMessageRequest([Required] string Content);
