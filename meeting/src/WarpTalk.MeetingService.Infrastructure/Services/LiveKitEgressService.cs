using System.IdentityModel.Tokens.Jwt;
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

/// <summary>
/// Hand-rolled client for LiveKit's Twirp-based Egress API (StartRoomCompositeEgress /
/// StopEgress) — see ILiveKitEgressService for why this doesn't use a LiveKit SDK package.
/// NOT verified end-to-end against a real LiveKit server in this environment; only the
/// service-layer logic that calls into this (MeetingRoomService.SetRecordingAsync) is
/// unit-tested, with this interface mocked out.
/// </summary>
public class LiveKitEgressService : ILiveKitEgressService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _host;
    private readonly ILogger<LiveKitEgressService> _logger;

    public LiveKitEgressService(HttpClient httpClient, IConfiguration configuration, ILogger<LiveKitEgressService> logger)
    {
        _httpClient = httpClient;
        _apiKey = configuration["LiveKit:ApiKey"] ?? throw new ArgumentNullException("LiveKit:ApiKey");
        _apiSecret = configuration["LiveKit:ApiSecret"] ?? throw new ArgumentNullException("LiveKit:ApiSecret");
        _host = (configuration["LiveKit:Host"] ?? "http://localhost:7880").TrimEnd('/');
        _logger = logger;
    }

    public async Task<Result<string>> StartRoomCompositeEgressAsync(string roomName, CancellationToken ct = default)
    {
        try
        {
            var filepath = $"recordings/{roomName}-{DateTime.UtcNow:yyyyMMddHHmmss}.mp4";
            var payload = new
            {
                room_name = roomName,
                layout = "grid",
                audio_only = false,
                file_outputs = new[] { new { filepath } }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_host}/twirp/livekit.Egress/StartRoomCompositeEgress")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GenerateServiceToken());

            using var response = await _httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("LiveKit StartRoomCompositeEgress failed ({Status}): {Body}", response.StatusCode, body);
                return Result.Failure<string>($"LiveKit Egress start failed: {response.StatusCode}", "LIVEKIT_EGRESS_START_FAILED");
            }

            using var doc = JsonDocument.Parse(body);
            var egressId = TryGetProperty(doc.RootElement, "egressId") ?? TryGetProperty(doc.RootElement, "egress_id");
            if (string.IsNullOrEmpty(egressId))
            {
                return Result.Failure<string>("LiveKit Egress response did not include an egress id.", "LIVEKIT_EGRESS_START_FAILED");
            }

            return Result.Success(egressId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start LiveKit RoomComposite Egress for room {RoomName}", roomName);
            return Result.Failure<string>($"Failed to start recording: {ex.Message}", "LIVEKIT_EGRESS_START_FAILED");
        }
    }

    public async Task<Result<bool>> StopEgressAsync(string egressId, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_host}/twirp/livekit.Egress/StopEgress")
            {
                Content = JsonContent.Create(new { egress_id = egressId })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GenerateServiceToken());

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("LiveKit StopEgress failed ({Status}): {Body}", response.StatusCode, body);
                return Result.Failure<bool>($"LiveKit Egress stop failed: {response.StatusCode}", "LIVEKIT_EGRESS_STOP_FAILED");
            }

            return Result.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop LiveKit Egress {EgressId}", egressId);
            return Result.Failure<bool>($"Failed to stop recording: {ex.Message}", "LIVEKIT_EGRESS_STOP_FAILED");
        }
    }

    private static string? TryGetProperty(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) ? value.GetString() : null;

    /// <summary>
    /// Server-API access token — a "roomRecord" video grant, distinct from the per-participant
    /// join tokens LiveKitTokenService issues (canPublish/canSubscribe/room-scoped).
    /// </summary>
    private string GenerateServiceToken()
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_apiSecret));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var videoGrant = new Dictionary<string, object>
        {
            { "roomRecord", true }
        };

        var payload = new JwtPayload(
            issuer: _apiKey,
            audience: null,
            claims: new List<Claim>(),
            notBefore: null,
            expires: DateTime.UtcNow.AddMinutes(10));
        payload.Add("sub", "meeting-service");
        payload.Add("video", videoGrant);

        var header = new JwtHeader(credentials);
        var token = new JwtSecurityToken(header, payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
