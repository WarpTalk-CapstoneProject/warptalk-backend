using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Services.Interface;

namespace WarpTalk.BillingService.Application.Services;

public class WorkspaceOwnershipResolver : IWorkspaceOwnershipResolver
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkspaceOwnershipResolver> _logger;

    public WorkspaceOwnershipResolver(
        IConfiguration configuration,
        ILogger<WorkspaceOwnershipResolver> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    // ======================================================
    // OWNER RESOLUTION
    // ======================================================
    public Task<Guid> ResolveOwnerUserIdAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var ownerMap = _configuration.GetSection("Billing:WorkspaceOwnerMap");

        var mappedOwner = ownerMap.GetChildren()
            .FirstOrDefault(x => x.Key == workspaceId.ToString())
            ?.Value;

        if (!string.IsNullOrWhiteSpace(mappedOwner) &&
            Guid.TryParse(mappedOwner, out var ownerId))
        {
            return Task.FromResult(ownerId);
        }

        var env = _configuration["ASPNETCORE_ENVIRONMENT"];

        if (string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Missing owner mapping for workspace {WorkspaceId}, fallback to workspaceId (DEV only)",
                workspaceId);

            return Task.FromResult(workspaceId);
        }

        throw new InvalidOperationException(
            $"Workspace owner mapping not configured for {workspaceId}");
    }

    // ======================================================
    // ACCESS CHECK (SIMPLE DEFAULT)
    // ======================================================
    public Task<bool> HasAccessAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        // MVP logic: owner has access
        return Task.FromResult(workspaceId == userId);
    }

    public Task<bool> IsOwnerAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        // MVP logic: same rule
        return Task.FromResult(workspaceId == userId);
    }
}