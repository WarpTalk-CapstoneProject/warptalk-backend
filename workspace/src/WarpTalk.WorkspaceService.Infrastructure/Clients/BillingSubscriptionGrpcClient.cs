using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Protos;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;

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

    public async Task<string?> ApplyWorkspaceEntitlementOverridesAsync(
        Guid workspaceId,
        IReadOnlyDictionary<string, string> overrides,
        Guid setByUserId,
        CancellationToken ct = default)
    {
        if (overrides.Count == 0)
        {
            return null;
        }

        try
        {
            var request = new ApplyWorkspaceEntitlementOverridesRequest
            {
                WorkspaceId = workspaceId.ToString(),
                SetByUserId = setByUserId.ToString()
            };

            foreach (var (key, value) in overrides)
            {
                request.Overrides.Add(new WorkspaceEntitlementOverrideItem
                {
                    EntitlementKey = key,
                    Value = value
                });
            }

            var response = await _client.ApplyWorkspaceEntitlementOverridesAsync(request, cancellationToken: ct);
            return response.Accepted ? null : response.ErrorMessage;
        }
        catch (Exception ex)
        {
            // Returns null (accepted) on an outage, and that is the correct direction HERE even
            // though the WT-262 read path could not do the same. This is a write, not a gate: the
            // owner's settings save must not fail because billing is down, and the value they chose
            // is a TIGHTENING — the workspace keeps the looser limit it already had until the push
            // succeeds, so an outage cannot grant anybody anything.
            _logger.LogError(
                ex,
                "Failed to push workspace entitlement overrides for workspace {WorkspaceId}; the previous entitlements remain in force.",
                workspaceId);
            return null;
        }
    }
}
