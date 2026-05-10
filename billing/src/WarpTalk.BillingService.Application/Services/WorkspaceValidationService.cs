using System.Security.Claims;
using WarpTalk.BillingService.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WarpTalk.BillingService.Application.Services;

public class WorkspaceValidationServices : IWorkspaceValidationService
{
    private readonly IConfiguration _config;
    private readonly ILogger<WorkspaceValidationServices> _logger;

    public WorkspaceValidationServices(IConfiguration config, ILogger<WorkspaceValidationServices> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task ValidateAsync(Guid workspaceId, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Workspace ID cannot be empty", nameof(workspaceId));

        // Skip validation if configured (e.g., for local dev/testing)
        if (bool.TryParse(_config["BILLING_SKIP_WORKSPACE_VALIDATION"], out var skipValidation) && skipValidation)
        {
            _logger.LogInformation("Workspace validation skipped for {WorkspaceId}", workspaceId);
            return;
        }

        var userId = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user?.FindFirst("sub")?.Value;
        if (user?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(userId))
            throw new UnauthorizedAccessException("User not authenticated");

        // For local development: if gRPC endpoint not configured, allow all authenticated users
        var grpcEndpoint = _config["Auth:GrpcEndpoint"];
        if (string.IsNullOrWhiteSpace(grpcEndpoint))
        {
            _logger.LogWarning("Auth:GrpcEndpoint not configured, allowing all authenticated users for workspace {WorkspaceId}", workspaceId);
            return;
        }

        // In production, validate via gRPC (TODO: implement when protos available)
        _logger.LogInformation("User {UserId} accessing workspace {WorkspaceId}", userId, workspaceId);
        await Task.CompletedTask; // Placeholder for actual gRPC call
    }
}
