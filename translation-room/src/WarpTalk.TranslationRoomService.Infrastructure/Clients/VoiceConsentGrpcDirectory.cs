using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Clients;

public sealed class VoiceConsentGrpcDirectory : IVoiceConsentDirectory
{
    private readonly UserService.UserServiceClient _client;
    private readonly ILogger<VoiceConsentGrpcDirectory> _logger;

    public VoiceConsentGrpcDirectory(
        UserService.UserServiceClient client,
        ILogger<VoiceConsentGrpcDirectory> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<bool> HasVoiceCloneConsentAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.HasVoiceCloneConsentAsync(
                new GetUserRequest { Id = userId.ToString() },
                cancellationToken: ct);

            return response.Granted;
        }
        catch (RpcException ex)
        {
            // Fail closed, and say so loudly.
            //
            // Everywhere else in this codebase an unreachable directory degrades to a sensible
            // default and the meeting carries on — a missing display name becomes "Participant",
            // missing language defaults fall back to the room's. This one cannot: the default
            // here would be permission to build a biometric model of somebody's voice, and
            // "AuthService was briefly down" is not something a person consented to.
            //
            // The consequence is real and worth stating: while AuthService is unreachable, nobody
            // can newly enable voice cloning. Their meeting still runs, still translates, still
            // dubs — in a library voice. That is the correct thing to lose.
            _logger.LogWarning(
                ex,
                "Could not reach AuthService for voice clone consent of {UserId}; treating as not granted.",
                userId);
            return false;
        }
    }
}
