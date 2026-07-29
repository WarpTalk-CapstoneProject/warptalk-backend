using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Protos;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Clients;

public sealed class BillingSubscriptionGrpcClient : IBillingSubscriptionClient
{
    private readonly BillingService.BillingServiceClient _client;
    private readonly ILogger<BillingSubscriptionGrpcClient> _logger;

    public BillingSubscriptionGrpcClient(
        BillingService.BillingServiceClient client,
        ILogger<BillingSubscriptionGrpcClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<bool> IsWorkspaceOnActiveTrialAsync(Guid workspaceId, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetActiveSubscriptionAsync(
                new GetActiveSubscriptionRequest { WorkspaceId = workspaceId.ToString() },
                cancellationToken: ct);

            return string.Equals(response.Status, "active", StringComparison.OrdinalIgnoreCase)
                   && DateTime.TryParse(response.TrialEndsAt, out var trialEndsAt)
                   && trialEndsAt > DateTime.UtcNow;
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.NotFound or StatusCode.Unavailable)
        {
            _logger.LogDebug(ex, "Billing subscription lookup did not find an active trial for workspace {WorkspaceId}.", workspaceId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Billing subscription lookup failed for workspace {WorkspaceId}; invite limit will not apply.", workspaceId);
            return false;
        }
    }
}
