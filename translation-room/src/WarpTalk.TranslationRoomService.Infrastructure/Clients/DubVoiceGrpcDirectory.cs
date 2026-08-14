using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Clients;

public sealed class DubVoiceGrpcDirectory : IDubVoiceDirectory
{
    private readonly UserService.UserServiceClient _client;
    private readonly ILogger<DubVoiceGrpcDirectory> _logger;

    public DubVoiceGrpcDirectory(
        UserService.UserServiceClient client,
        ILogger<DubVoiceGrpcDirectory> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<string?> GetDubVoiceAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetPreferredVoiceAsync(
                new GetUserRequest { Id = userId.ToString() },
                cancellationToken: ct);

            return string.IsNullOrWhiteSpace(response.VoiceId) ? null : response.VoiceId;
        }
        catch (RpcException ex)
        {
            // Degrades, unlike its neighbour VoiceConsentGrpcDirectory, and the difference is the
            // point. That one guards biometric processing and must fail closed. This one only
            // says WHICH voice somebody preferred; losing the answer costs them their chosen
            // voice for this meeting and nothing else, so the meeting carries on with the voice
            // it would have used before this feature existed.
            _logger.LogWarning(
                ex,
                "Could not reach AuthService for the dub voice of {UserId}; falling back to live cloning.",
                userId);
            return null;
        }
    }
}
