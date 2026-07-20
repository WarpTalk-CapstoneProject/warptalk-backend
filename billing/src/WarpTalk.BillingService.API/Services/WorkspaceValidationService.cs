using System.Security.Claims;
using Grpc.Core;
using Grpc.Net.Client;

namespace WarpTalk.BillingService.API.Services;

public class WorkspaceValidationService : IWorkspaceValidationService, IDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<WorkspaceValidationService> _logger;

    public WorkspaceValidationService(IConfiguration config, ILogger<WorkspaceValidationService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task ValidateAsync(Guid workspaceId, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Workspace ID cannot be empty", nameof(workspaceId));

        // Skip validation if configured (e.g., for local dev/testing)
        if (_config.GetValue<bool>("BILLING_SKIP_WORKSPACE_VALIDATION", false))
        {
            _logger.LogInformation("Workspace validation skipped for {WorkspaceId}", workspaceId);
            return;
        }

        var userId = user?.FindFirstValue(ClaimTypes.NameIdentifier) ?? user?.FindFirstValue("sub");
        if (user?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(userId))
            throw new UnauthorizedAccessException("User not authenticated");

        // For local development: if gRPC endpoint not configured, allow all authenticated users
        var grpcEndpoint = _config.GetValue<string>("Auth:GrpcEndpoint");
        if (string.IsNullOrWhiteSpace(grpcEndpoint))
        {
            _logger.LogWarning("Auth:GrpcEndpoint not configured, allowing all authenticated users for workspace {WorkspaceId}", workspaceId);
            return;
        }

        // In production, validate via gRPC (TODO: implement when protos available)
        _logger.LogInformation("User {UserId} accessing workspace {WorkspaceId}", userId, workspaceId);
        await Task.CompletedTask; // Placeholder for actual gRPC call
    }

    public void Dispose()
    {
        // No resources to dispose
    }
}