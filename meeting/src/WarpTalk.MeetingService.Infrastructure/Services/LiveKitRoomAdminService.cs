using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Infrastructure.Services;

public sealed class LiveKitRoomAdminService : ILiveKitRoomAdminService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _host;
    private readonly ILogger<LiveKitRoomAdminService> _logger;

    public LiveKitRoomAdminService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<LiveKitRoomAdminService> logger)
    {
        _httpClient = httpClient;
        _apiKey = configuration["LiveKit:ApiKey"]
            ?? throw new ArgumentNullException("LiveKit:ApiKey");
        _apiSecret = configuration["LiveKit:ApiSecret"]
            ?? throw new ArgumentNullException("LiveKit:ApiSecret");
        _host = ToHttpApiUrl(
            configuration["LiveKit:Url"] ?? configuration["LiveKit:Host"]
            ?? throw new InvalidOperationException("LiveKit:Url is required."));
        _logger = logger;
    }

    public Task<Result<bool>> RemoveParticipantAsync(
        string roomName,
        string participantIdentity,
        CancellationToken ct = default) =>
        SendRoomCommandAsync(
            "RemoveParticipant",
            new { room = roomName, identity = participantIdentity },
            roomName,
            requiresRoomCreate: false,
            ct);

    /// <summary>
    /// Mutes every microphone track the participant is publishing.
    ///
    /// LiveKit's MutePublishedTrack needs a track sid, which the caller does not have and
    /// must not be trusted for: a browser's view of somebody else's tracks goes stale on
    /// every republish, and a wrong sid silently mutes nothing. So the sid is resolved here,
    /// from the SFU's own answer to "what is this participant publishing right now".
    ///
    /// Every microphone track, not the first: a participant republishing mid-call can briefly
    /// have two, and muting one of them leaves the room still hearing the other.
    /// </summary>
    public async Task<Result<bool>> MuteParticipantMicrophoneAsync(
        string roomName,
        string participantIdentity,
        CancellationToken ct = default)
    {
        var participant = await SendRoomQueryAsync(
            "GetParticipant",
            new { room = roomName, identity = participantIdentity },
            roomName,
            ct);
        if (!participant.IsSuccess)
            return Result.Failure<bool>(participant.Error!, participant.ErrorCode);

        var trackSids = ReadMicrophoneTrackSids(participant.Value ?? string.Empty);
        if (trackSids.Count == 0)
        {
            // Nobody is publishing, so the room is already not hearing them. Reporting this as
            // a failure would make the host press a button that says it did not work when the
            // outcome they asked for already holds.
            _logger.LogInformation(
                "Mute requested for {Identity} in {RoomName}, who has no live microphone track.",
                participantIdentity,
                roomName);
            return Result.Success(true);
        }

        foreach (var trackSid in trackSids)
        {
            var muted = await SendRoomCommandAsync(
                "MutePublishedTrack",
                new { room = roomName, identity = participantIdentity, track_sid = trackSid, muted = true },
                roomName,
                requiresRoomCreate: false,
                ct);
            if (!muted.IsSuccess)
                return muted;
        }

        return Result.Success(true);
    }

    /// <summary>
    /// Twirp JSON answers in camelCase, but LiveKit deployments have been seen to answer in
    /// snake_case for the same field. Both spellings are read rather than guessing one — a
    /// miss here is a mute button that silently does nothing.
    /// </summary>
    private static List<string> ReadMicrophoneTrackSids(string participantJson)
    {
        var sids = new List<string>();
        if (string.IsNullOrWhiteSpace(participantJson)) return sids;

        using var document = JsonDocument.Parse(participantJson);
        if (!document.RootElement.TryGetProperty("tracks", out var tracks) ||
            tracks.ValueKind != JsonValueKind.Array)
            return sids;

        foreach (var track in tracks.EnumerateArray())
        {
            var source = ReadString(track, "source") ?? string.Empty;
            var type = ReadString(track, "type") ?? string.Empty;
            var isMicrophone =
                source.Equals("MICROPHONE", StringComparison.OrdinalIgnoreCase) ||
                (source.Length == 0 && type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase));
            if (!isMicrophone) continue;

            var sid = ReadString(track, "sid");
            if (!string.IsNullOrEmpty(sid)) sids.Add(sid);
        }

        return sids;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task<Result<string>> SendRoomQueryAsync(
        string command,
        object payload,
        string roomName,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_host}/twirp/livekit.RoomService/{command}")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                GenerateRoomServiceToken(roomName, requiresRoomCreate: false));

            using var response = await _httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
                return Result.Success(body);

            _logger.LogError(
                "LiveKit room query {Command} failed ({Status}): {Body}",
                command,
                response.StatusCode,
                body);
            return Result.Failure<string>(
                $"LiveKit {command} failed: {response.StatusCode}",
                "LIVEKIT_ROOM_COMMAND_FAILED");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LiveKit room query {Command} failed for room {RoomName}", command, roomName);
            return Result.Failure<string>(
                $"LiveKit {command} failed: {ex.Message}",
                "LIVEKIT_ROOM_COMMAND_FAILED");
        }
    }

    public Task<Result<bool>> DeleteRoomAsync(
        string roomName,
        CancellationToken ct = default) =>
        SendRoomCommandAsync(
            "DeleteRoom",
            new { room = roomName },
            roomName,
            requiresRoomCreate: true,
            ct);

    private async Task<Result<bool>> SendRoomCommandAsync(
        string command,
        object payload,
        string roomName,
        bool requiresRoomCreate,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_host}/twirp/livekit.RoomService/{command}")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                GenerateRoomServiceToken(roomName, requiresRoomCreate));

            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                return Result.Success(true);

            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "LiveKit room command {Command} failed ({Status}): {Body}",
                command,
                response.StatusCode,
                body);
            return Result.Failure<bool>(
                $"LiveKit {command} failed: {response.StatusCode}",
                "LIVEKIT_ROOM_COMMAND_FAILED");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "LiveKit room command {Command} failed for room {RoomName}",
                command,
                roomName);
            return Result.Failure<bool>(
                $"LiveKit {command} failed: {ex.Message}",
                "LIVEKIT_ROOM_COMMAND_FAILED");
        }
    }

    private string GenerateRoomServiceToken(string roomName, bool requiresRoomCreate)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_apiSecret));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
        // LiveKit separates room lifecycle permission from participant administration:
        // DeleteRoom requires roomCreate, while RemoveParticipant requires roomAdmin scoped
        // to one room. Reusing roomAdmin for DeleteRoom is rejected as Unauthorized.
        var videoGrant = requiresRoomCreate
            ? new Dictionary<string, object> { ["roomCreate"] = true }
            : new Dictionary<string, object>
            {
                ["roomAdmin"] = true,
                ["room"] = roomName
            };
        var payload = new JwtPayload(
            issuer: _apiKey,
            audience: null,
            claims: new List<Claim>(),
            notBefore: null,
            expires: DateTime.UtcNow.AddMinutes(10));
        payload.Add("sub", "meeting-service");
        payload.Add("video", videoGrant);

        return new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(new JwtHeader(credentials), payload));
    }

    private static string ToHttpApiUrl(string configuredUrl)
    {
        var normalized = configuredUrl.Trim().TrimEnd('/');
        if (normalized.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            return $"https://{normalized[6..]}";
        if (normalized.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            return $"http://{normalized[5..]}";
        if (normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return normalized;

        throw new InvalidOperationException(
            "LiveKit:Url must use ws, wss, http, or https.");
    }
}
