using System.Globalization;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;
using WarpTalk.Shared.Protos;

namespace WarpTalk.Shared.AdminAudit;

/// <summary>One entry for the platform audit log, as a service describes it.</summary>
public sealed record AdminAuditRecord
{
    public required string Action { get; init; }
    public required string EntityType { get; init; }
    public Guid? EntityId { get; init; }
    public string? EntityKey { get; init; }
    public string? EntityLabel { get; init; }
    public Guid? WorkspaceId { get; init; }
    public required Guid ActorId { get; init; }
    public string? Reason { get; init; }
    public string Result { get; init; } = AdminAuditResults.Succeeded;
    public string? ErrorMessage { get; init; }
    public required string CorrelationId { get; init; }
    public IReadOnlyDictionary<string, string?>? BeforeSummary { get; init; }
    public IReadOnlyDictionary<string, string?>? AfterSummary { get; init; }
    public AdminAuditRequestMetadata Metadata { get; init; } = AdminAuditRequestMetadata.Empty;
}

/// <summary>Where a service sends its <see cref="AdminAuditRecord"/>s.</summary>
public interface IAdminAuditSink
{
    /// <summary>True only when the store confirmed the entry. Never throws.</summary>
    Task<bool> RecordAsync(AdminAuditRecord record, CancellationToken ct = default);
}

/// <summary>
/// The platform audit store over gRPC (<c>admin_audit.proto</c>, hosted by the workspace service) —
/// the same transport auth, billing, the language catalog and the plugin catalog already use.
/// </summary>
public sealed class GrpcAdminAuditSink : IAdminAuditSink
{
    public const int MaxErrorLength = 1000;

    private readonly AdminAuditService.AdminAuditServiceClient _client;
    private readonly string _sourceService;
    private readonly ILogger<GrpcAdminAuditSink> _logger;
    private readonly TimeProvider _time;

    public GrpcAdminAuditSink(
        AdminAuditService.AdminAuditServiceClient client,
        string sourceService,
        ILogger<GrpcAdminAuditSink> logger,
        TimeProvider? time = null)
    {
        _client = client;
        _sourceService = sourceService;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<bool> RecordAsync(AdminAuditRecord record, CancellationToken ct = default)
    {
        var request = ToRequest(record, _sourceService, _time.GetUtcNow().UtcDateTime);
        try
        {
            var response = await _client.RecordAdminActionAsync(request, cancellationToken: ct);
            if (response.Recorded) return true;

            _logger.LogError(
                "Admin audit refused. Action: {Action}, Entity: {EntityType}/{EntityId}, Reason: {Error}",
                record.Action, record.EntityType, record.EntityId?.ToString() ?? record.EntityKey, response.ErrorMessage);
            return false;
        }
        catch (RpcException ex)
        {
            _logger.LogError(
                ex, "Admin audit call failed. Action: {Action}, Status: {Status}", record.Action, ex.Status);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Admin audit call failed. Action: {Action}", record.Action);
            return false;
        }
    }

    public static RecordAdminActionRequest ToRequest(AdminAuditRecord record, string sourceService, DateTime now)
    {
        var request = new RecordAdminActionRequest
        {
            SourceService = sourceService,
            Action = record.Action,
            EntityType = record.EntityType,
            EntityId = record.EntityId is { } id && id != Guid.Empty ? id.ToString() : string.Empty,
            WorkspaceId = record.WorkspaceId is { } ws && ws != Guid.Empty ? ws.ToString() : string.Empty,
            ActorId = record.ActorId.ToString(),
            Reason = record.Reason?.Trim() ?? string.Empty,
            Result = record.Result,
            PerformedAt = now.ToString("O", CultureInfo.InvariantCulture),
            CorrelationId = record.CorrelationId,
            EntityKey = record.EntityKey ?? string.Empty,
            EntityLabel = AdminAuditRequestMetadata.Bound(record.EntityLabel, 200) ?? string.Empty,
            ErrorMessage = AdminAuditRequestMetadata.Bound(record.ErrorMessage, MaxErrorLength) ?? string.Empty,
        };
        record.Metadata.ApplyTo(request);
        Fill(request.BeforeSummary, AdminAuditRedaction.Redact(record.BeforeSummary));
        Fill(request.AfterSummary, AdminAuditRedaction.Redact(record.AfterSummary));
        return request;
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
