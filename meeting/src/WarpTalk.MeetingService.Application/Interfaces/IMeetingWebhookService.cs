using System.Text.Json;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface IMeetingWebhookService
{
    Task<Result<bool>> ProcessWebhookAsync(JsonElement payload);
}
