namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>One Notification Center row, and the realtime toast that comes with it.</summary>
public record UserNotification(
    Guid UserId,
    string Type,
    string Title,
    string Body,
    string ActionUrl,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// Sends through the notification service's SendNotification RPC, which persists the row and
/// publishes the realtime event.
/// </summary>
public interface IUserNotificationClient
{
    /// <summary>
    /// Best-effort: never throws. Returns whether the notification service accepted it - which is
    /// the one signal that catches a type missing from its validator, so the caller logs a refusal.
    /// </summary>
    Task<bool> SendAsync(UserNotification notification, CancellationToken ct = default);
}
