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
    private readonly object? _s3Output;
    private readonly ILogger<LiveKitEgressService> _logger;

    public LiveKitEgressService(HttpClient httpClient, IConfiguration configuration, ILogger<LiveKitEgressService> logger)
    {
        _httpClient = httpClient;
        _apiKey = configuration["LiveKit:ApiKey"] ?? throw new ArgumentNullException("LiveKit:ApiKey");
        _apiSecret = configuration["LiveKit:ApiSecret"] ?? throw new ArgumentNullException("LiveKit:ApiSecret");
        var configuredUrl = configuration["LiveKit:Url"] ?? configuration["LiveKit:Host"]
            ?? throw new InvalidOperationException("LiveKit:Url is required.");
        _host = ToHttpApiUrl(configuredUrl);
        _s3Output = BuildS3Output(configuration);
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
                file_outputs = new[] { new { filepath, s3 = _s3Output } }
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

                // A quota refusal is not a fault, and saying "Internal Server Error" about it
                // sends whoever pressed Record to look for a bug that does not exist. LiveKit
                // answers an exhausted plan with 429 and {"code":"resource_exhausted"} — which is
                // what production returned all morning while the host was told only "Could not
                // start recording."
                if (body.Contains("resource_exhausted", StringComparison.OrdinalIgnoreCase)
                    || response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return Result.Failure<string>(
                        "Recording is unavailable: this workspace's video-recording minutes are used up. "
                        + "The meeting itself, its transcript and its translation are unaffected.",
                        "LIVEKIT_EGRESS_QUOTA_EXCEEDED");
                }

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

    /// <inheritdoc />
    public async Task<Result<JsonElement?>> GetEgressAsync(string egressId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(egressId))
            return Result.Success<JsonElement?>(null);

        try
        {
            // Filtered server-side by egress_id rather than listing everything and matching
            // here: an account that has recorded for months would otherwise pay to serialise its
            // whole egress history on every reconciliation tick.
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_host}/twirp/livekit.Egress/ListEgress")
            {
                Content = JsonContent.Create(new { egress_id = egressId })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GenerateServiceToken());

            using var response = await _httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "LiveKit ListEgress failed ({Status}) for {EgressId}: {Body}",
                    response.StatusCode,
                    egressId,
                    body);
                return Result.Failure<JsonElement?>(
                    $"LiveKit ListEgress failed: {response.StatusCode}",
                    "LIVEKIT_EGRESS_LIST_FAILED");
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                // An empty/absent list is LiveKit saying "I have no such egress", which is a real
                // answer the caller acts on — not an error to retry.
                return Result.Success<JsonElement?>(null);
            }

            foreach (var item in items.EnumerateArray())
            {
                // Clone: `item` points into `doc`, which this method disposes on the way out.
                // Returning it unclone­d hands the caller a JsonElement whose backing buffer has
                // already been returned to the pool.
                return Result.Success<JsonElement?>(item.Clone());
            }

            return Result.Success<JsonElement?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read LiveKit Egress {EgressId}", egressId);
            return Result.Failure<JsonElement?>($"Failed to read egress: {ex.Message}", "LIVEKIT_EGRESS_LIST_FAILED");
        }
    }

    private static string? TryGetProperty(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) ? value.GetString() : null;

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

        throw new InvalidOperationException("LiveKit:Url must use ws, wss, http, or https.");
    }

    private static object? BuildS3Output(IConfiguration configuration)
    {
        var bucket = configuration["LiveKit:Egress:S3:Bucket"];
        if (string.IsNullOrWhiteSpace(bucket))
            return null;

        var accessKey = configuration["LiveKit:Egress:S3:AccessKey"]
            ?? throw new InvalidOperationException("LiveKit:Egress:S3:AccessKey is required when recording is enabled.");
        var secret = configuration["LiveKit:Egress:S3:Secret"]
            ?? throw new InvalidOperationException("LiveKit:Egress:S3:Secret is required when recording is enabled.");
        var endpoint = configuration["LiveKit:Egress:S3:Endpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint) &&
            !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("LiveKit Cloud Egress S3 endpoint must use HTTPS.");
        }

        return new
        {
            access_key = accessKey,
            secret,
            bucket,
            region = configuration["LiveKit:Egress:S3:Region"],
            endpoint,
            force_path_style = !string.IsNullOrWhiteSpace(endpoint)
        };
    }

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
