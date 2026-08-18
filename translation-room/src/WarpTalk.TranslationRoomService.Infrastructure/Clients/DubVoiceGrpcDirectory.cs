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

    public async Task<DubVoiceSelection> GetSelectionAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetPreferredVoiceAsync(
                new GetUserRequest { Id = userId.ToString() },
                cancellationToken: ct);

            return new DubVoiceSelection(
                Trimmed(response.VoiceId),
                Trimmed(response.AutoCloneVoiceId),
                // Passed through as the text AuthService formatted, never parsed and re-formatted
                // here. Round-tripping it through a decimal would put it at the mercy of this
                // server's culture, and empty has to stay distinguishable from zero.
                Trimmed(response.AutoCloneScore));
        }
        catch (RpcException ex)
        {
            // Degrades, unlike its neighbour VoiceConsentGrpcDirectory, and the difference is the
            // point. That one guards biometric processing and must fail closed. This one only
            // says WHICH voice somebody should be given; losing the answer costs them their
            // chosen voice for this meeting, and costs a carried-over clone one re-clone, so the
            // meeting carries on with the voice it would have used before this feature existed.
            _logger.LogWarning(
                ex,
                "Could not reach AuthService for the voice selection of {UserId}; falling back to live cloning.",
                userId);
            return DubVoiceSelection.None;
        }
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
