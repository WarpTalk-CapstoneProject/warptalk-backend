using System;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

public static class RedisKeyHelper
{
    public static string GetTelemetryStateKey(Guid roomId) => $"translationRoom:{roomId}:telemetry_state";
    
    public static string GetTranscriptKey(Guid roomId) => $"translationRoom:{roomId}:transcript";
}
