using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
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
