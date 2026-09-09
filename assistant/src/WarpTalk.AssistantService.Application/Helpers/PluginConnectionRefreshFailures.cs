using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Helpers;

internal static class PluginConnectionRefreshFailures
{
    internal static Result Transient(string errorCode, string message)
    {
        return Result.Failure(message, errorCode);
    }
}
