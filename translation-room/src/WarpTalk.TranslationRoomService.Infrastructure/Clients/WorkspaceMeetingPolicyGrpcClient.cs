using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Clients;

public sealed class WorkspaceMeetingPolicyGrpcClient : IWorkspaceMeetingPolicy
{
    private const string UnavailableMessage =
        "Could not verify your permission to create meetings. Please try again in a moment.";

    private readonly WorkspaceService.WorkspaceServiceClient _client;
    private readonly ILogger<WorkspaceMeetingPolicyGrpcClient> _logger;

    public WorkspaceMeetingPolicyGrpcClient(
        WorkspaceService.WorkspaceServiceClient client,
        ILogger<WorkspaceMeetingPolicyGrpcClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<Result> ValidateMeetingCreationAsync(
        Guid workspaceId,
        Guid userId,
        IEnumerable<string> targetLanguages,
        CancellationToken ct = default)
    {
        var request = new ValidateMeetingCreationRequest
        {
            WorkspaceId = workspaceId.ToString(),
            UserId = userId.ToString()
        };
        request.TargetLanguages.AddRange(targetLanguages ?? Enumerable.Empty<string>());

        ValidateMeetingCreationResponse response;
        try
        {
            response = await _client.ValidateMeetingCreationAsync(request, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Fails closed on purpose: this call IS the permission gate, so letting an outage
            // through would reopen WT-249. Surfaced as Unavailable rather than Forbidden so the
            // caller can tell "you may not" apart from "we could not check".
            _logger.LogError(
                ex,
                "Meeting-creation policy check failed. WorkspaceId: {WorkspaceId}, UserId: {UserId}",
                workspaceId,
                userId);
            return Result.Failure(UnavailableMessage, ErrorCodes.ServiceUnavailable);
        }

        return response.IsAllowed
            ? Result.Success()
            : Result.Failure(response.ErrorMessage, ErrorCodes.Forbidden);
    }
}
