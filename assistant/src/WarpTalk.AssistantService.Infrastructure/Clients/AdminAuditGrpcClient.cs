using System.Globalization;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;
using WarpTalk.Shared.Protos;

namespace WarpTalk.AssistantService.Infrastructure.Clients;

/// <summary>
/// Writes the marketplace's admin actions into the platform audit log over gRPC - the contract auth
/// and translation-room already use. Every failure comes back as a failed <see cref="Result"/>;
/// nothing is swallowed, because the caller abandons its change when the record fails.
/// </summary>
public sealed class AdminAuditGrpcClient : IAdminAuditRecorder
{
    private readonly AdminAuditService.AdminAuditServiceClient _client;
    private readonly ILogger<AdminAuditGrpcClient> _logger;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public AdminAuditGrpcClient(
        AdminAuditService.AdminAuditServiceClient client,
        ILogger<AdminAuditGrpcClient> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _client = client;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<Result> RecordPluginActionAsync(
        string action,
        Guid entityId,
        Guid actorId,
        IReadOnlyDictionary<string, string?>? beforeSummary,
        IReadOnlyDictionary<string, string?>? afterSummary,
        CancellationToken ct = default)
    {
        var request = new RecordAdminActionRequest
        {
            SourceService = AdminAuditSources.AssistantService,
            Action = action,
            EntityType = AdminAuditEntityTypes.Plugin,
            EntityId = entityId.ToString(),
            // The marketplace is platform-wide: no workspace. Empty rather than plausible.
            WorkspaceId = string.Empty,
            ActorId = actorId.ToString(),
            Reason = string.Empty,
            Result = AdminAuditResults.Succeeded,
            PerformedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            // One record per request: an admin who repeats a change has made it twice.
            CorrelationId = Guid.NewGuid().ToString(),
        };

        Fill(request.BeforeSummary, beforeSummary);
        Fill(request.AfterSummary, afterSummary);

        // Who asked and from where, read from the admin's own request here — the store only ever
        // sees this service's gRPC call.
        AdminAuditRequestMetadata.FromHttpContext(_httpContextAccessor?.HttpContext).ApplyTo(request);

        try
        {
            var response = await _client.RecordAdminActionAsync(request, cancellationToken: ct);
            if (response.Recorded) return Result.Success();

            _logger.LogError("Admin audit refused. Action: {Action}, Reason: {Error}", action, response.ErrorMessage);
            return Result.Failure(
                "The change was not made because it could not be recorded in the audit log.",
                ErrorCodes.ServiceUnavailable);
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Admin audit call failed. Action: {Action}, Status: {Status}", action, ex.Status);
            return Result.Failure(
                "The change was not made because the audit log could not be reached.",
                ErrorCodes.ServiceUnavailable);
        }
    }

    public async Task<Result> RecordPluginWorkspaceActionAsync(
        string action,
        Guid pluginId,
        Guid workspaceId,
        Guid actorId,
        string? reason,
        IReadOnlyDictionary<string, string?>? beforeSummary,
        IReadOnlyDictionary<string, string?>? afterSummary,
        CancellationToken ct = default)
    {
        var request = new RecordAdminActionRequest
        {
            SourceService = AdminAuditSources.AssistantService,
            Action = action,
            EntityType = AdminAuditEntityTypes.Plugin,
            EntityId = pluginId.ToString(),
            // Unlike a catalog edit, this one is about a workspace: filed under it, so the
            // workspace's audit trail shows who turned the plugin on or off there.
            WorkspaceId = workspaceId.ToString(),
            ActorId = actorId.ToString(),
            Reason = reason ?? string.Empty,
            Result = AdminAuditResults.Succeeded,
            PerformedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            CorrelationId = Guid.NewGuid().ToString(),
            // The plugin's key, so the audit screen names the plugin rather than a bare row id.
            EntityKey = (beforeSummary ?? afterSummary)?.GetValueOrDefault("plugin_key") ?? string.Empty,
        };

        Fill(request.BeforeSummary, beforeSummary);
        Fill(request.AfterSummary, afterSummary);

        // Who asked and from where, read from the admin's own request (#450).
        AdminAuditRequestMetadata.FromHttpContext(_httpContextAccessor?.HttpContext).ApplyTo(request);

        try
        {
            var response = await _client.RecordAdminActionAsync(request, cancellationToken: ct);
            if (response.Recorded) return Result.Success();

            _logger.LogError("Admin audit refused. Action: {Action}, Reason: {Error}", action, response.ErrorMessage);
            return Result.Failure(
                "The change was not made because it could not be recorded in the audit log.",
                ErrorCodes.ServiceUnavailable);
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Admin audit call failed. Action: {Action}, Status: {Status}", action, ex.Status);
            return Result.Failure(
                "The change was not made because the audit log could not be reached.",
                ErrorCodes.ServiceUnavailable);
        }
    }

    /// <summary>proto3 maps hold no nulls, so a null value is dropped rather than written as "".</summary>
    private static void Fill(
        Google.Protobuf.Collections.MapField<string, string> target,
        IReadOnlyDictionary<string, string?>? source)
    {
        if (source is null) return;
        foreach (var (key, value) in source)
        {
            if (value is not null) target[key] = value;
        }
    }
}
