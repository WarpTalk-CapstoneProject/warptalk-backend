using System.Text.Json;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface IMeetingWebhookService
{
    bool ValidateWebhookToken(string token, string bodyText);
    Task<Result<bool>> ProcessWebhookAsync(JsonElement payload);
}
