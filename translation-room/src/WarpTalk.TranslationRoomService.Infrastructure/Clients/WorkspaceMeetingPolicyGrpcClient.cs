using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Clients;

public sealed class WorkspaceMeetingPolicyGrpcClient : IWorkspaceMeetingPolicy
{
    private const string UnavailableMessage =
        "Could not verify your permission to create meetings. Please try again in a moment.";

    private const string SuspendedMessage =
        "This workspace is suspended. Contact your administrator to restore it.";

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

    /// <inheritdoc />
    public async Task<Result> EnsureWorkspaceCanHostMeetingsAsync(
        Guid workspaceId,
        CancellationToken ct = default)
    {
        GetWorkspacePreflightResponse response;
        try
        {
            // UserEmail deliberately left empty: it only drives the verified-domain lookup on the
            // workspace side, which this caller has no use for and should not pay for on a path
            // that runs on every join.
            response = await _client.GetWorkspacePreflightDetailsAsync(
                new GetWorkspacePreflightRequest { WorkspaceId = workspaceId.ToString() },
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Fails OPEN, unlike ValidateMeetingCreationAsync above — see the interface docs. Join
            // and start carried no WorkspaceService dependency before this check existed, and a
            // WorkspaceService outage must not become "no meeting in the product can be entered".
            _logger.LogWarning(
                ex,
                "Workspace lifecycle check failed; allowing the request through. WorkspaceId: {WorkspaceId}",
                workspaceId);
            return Result.Success();
        }

        return response.IsActive
            ? Result.Success()
            : Result.Failure(SuspendedMessage, ErrorCodes.Forbidden);
    }

    /// <inheritdoc />
    public async Task<bool?> GetHostApprovalDefaultAsync(
        Guid workspaceId,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetWorkspaceSettingsAsync(
                new GetWorkspaceSettingsRequest { WorkspaceId = workspaceId.ToString() },
                cancellationToken: ct);

            return response.EnforceHostApprovalDefault;
        }
        catch (Exception ex)
        {
            // Null, not false. Returning false here would let a WorkspaceService outage silently
            // strip host approval from every meeting created during it — a security decision made
            // by a network error. Null hands the answer back to the meeting type, which is exactly
            // what decided it before this call existed.
            _logger.LogWarning(
                ex,
                "Could not read the workspace host-approval default; the meeting type's own default stands. WorkspaceId: {WorkspaceId}",
                workspaceId);
            return null;
        }
    }
}
