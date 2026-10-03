using System.Net;
using System.Text.Json;

namespace WarpTalk.MeetingService.Infrastructure.Services;

/// <summary>
/// Reading LiveKit's Twirp error bodies — <c>{"code":"not_found","msg":"…"}</c> — in one place, for
/// LiveKitRoomAdminService and LiveKitEgressService.
/// </summary>
public static class LiveKitTwirpErrors
{
    /// <summary>Whether the body is a Twirp error whose <c>code</c> is <c>not_found</c>. Never throws.</summary>
    public static bool HasNotFoundCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && string.Equals(code.GetString(), "not_found", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// LiveKit itself saying "I have no such thing": a 404 AND a Twirp <c>not_found</c> body. A 404
    /// from anything else — a proxy's HTML page, a wrong path, a misrouted host — is not an answer
    /// about the room or egress, and must never be read as "empty" or "already stopped".
    /// </summary>
    public static bool IsNotFound(HttpStatusCode status, string? body) =>
        status == HttpStatusCode.NotFound && HasNotFoundCode(body);
}
