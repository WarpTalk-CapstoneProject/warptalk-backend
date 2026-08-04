using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Services;

public class AdminAuditLogService : IAdminAuditLogService
{
    private const int MaxRangeDays = 366;

    private static readonly string[] AllowedResults =
    [
        AdminAuditResults.Succeeded,
        AdminAuditResults.Failed,
    ];

    private readonly IAdminAuditLogRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<AdminAuditLogService> _logger;

    public AdminAuditLogService(
        IAdminAuditLogRepository repository,
        IUnitOfWork unitOfWork,
        ILogger<AdminAuditLogService> logger)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<AdminPagedResult<AdminAuditLogEntryDto>>> QueryAsync(
        AdminAuditLogQuery query,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(query.Result)
            && !AllowedResults.Contains(query.Result, StringComparer.OrdinalIgnoreCase))
        {
            return Result.Failure<AdminPagedResult<AdminAuditLogEntryDto>>(
                "Unknown result filter. Expected 'succeeded' or 'failed'.", ErrorCodes.ValidationError);
        }

        if (query.From is { } from && query.To is { } to && from >= to)
        {
            return Result.Failure<AdminPagedResult<AdminAuditLogEntryDto>>(
                "'from' must be earlier than 'to'.", ErrorCodes.ValidationError);
        }

        if (query.From is { } rangeFrom && query.To is { } rangeTo
            && (rangeTo - rangeFrom).TotalDays > MaxRangeDays)
        {
            return Result.Failure<AdminPagedResult<AdminAuditLogEntryDto>>(
                $"Date range must not exceed {MaxRangeDays} days.", ErrorCodes.ValidationError);
        }

        var (page, pageSize) = query.Normalize();

        try
        {
            var (rows, total) = await _repository.QueryAsync(
                new AdminAuditLogFilter(
                    page,
                    pageSize,
                    query.ActorId,
                    query.Action?.Trim(),
                    query.EntityType?.Trim(),
                    query.EntityId,
                    query.WorkspaceId,
                    query.SourceService?.Trim(),
                    query.Result?.Trim().ToLowerInvariant(),
                    ToUtc(query.From),
                    ToUtc(query.To)),
                ct);

            var items = rows.Select(ToDto).ToList();
            return Result.Success(new AdminPagedResult<AdminAuditLogEntryDto>(items, page, pageSize, total));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin audit log query failed.");
            return Result.Failure<AdminPagedResult<AdminAuditLogEntryDto>>(
                "An unexpected error occurred while querying the audit log.", ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Appends an action published by another service. Idempotent on
    /// (source, correlation id, action, entity) so a redelivered message does not duplicate a row.
    /// </summary>
    public async Task<Result> RecordAsync(AdminActionRecordedEvent action, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(action.SourceService)
            || string.IsNullOrWhiteSpace(action.Action)
            || string.IsNullOrWhiteSpace(action.EntityType))
        {
            return Result.Failure(
                "source_service, action, and entity_type are required.", ErrorCodes.ValidationError);
        }

        var result = AllowedResults.Contains(action.Result, StringComparer.OrdinalIgnoreCase)
            ? action.Result.ToLowerInvariant()
            : AdminAuditResults.Succeeded;

        try
        {
            if (await _repository.ExistsAsync(
                    action.SourceService, action.CorrelationId, action.Action, action.EntityId, ct))
            {
                _logger.LogDebug(
                    "Skipping duplicate admin audit entry. Source: {Source}, CorrelationId: {CorrelationId}",
                    action.SourceService,
                    action.CorrelationId);
                return Result.Success();
            }

            await _repository.AppendAsync(
                new WorkspaceAdminAction
                {
                    Id = Guid.NewGuid(),
                    SourceService = action.SourceService,
                    Action = action.Action,
                    EntityType = action.EntityType,
                    EntityId = action.EntityId,
                    WorkspaceId = action.WorkspaceId,
                    PerformedBy = action.ActorId,
                    Reason = string.IsNullOrWhiteSpace(action.Reason) ? "(no reason given)" : action.Reason,
                    Result = result,
                    PerformedAt = action.PerformedAt == default
                        ? DateTime.UtcNow
                        : action.PerformedAt.ToUniversalTime(),
                    CorrelationId = action.CorrelationId,
                    // Redacted again here: the publisher is expected to redact, but a secret in
                    // an append-only table with no DELETE grant is not removable afterwards.
                    BeforeSummary = Serialize(AdminAuditRedaction.Redact(action.BeforeSummary)),
                    AfterSummary = Serialize(AdminAuditRedaction.Redact(action.AfterSummary)),
                },
                ct);

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to record admin audit entry. Source: {Source}, Action: {Action}",
                action.SourceService,
                action.Action);
            return Result.Failure("Failed to record the admin action.", ErrorCodes.InternalServerError);
        }
    }

    private static DateTime? ToUtc(DateTime? value) => value?.ToUniversalTime();

    private static string? Serialize(IReadOnlyDictionary<string, string?>? summary) =>
        summary is null || summary.Count == 0 ? null : JsonSerializer.Serialize(summary);

    public static AdminAuditLogEntryDto ToDto(WorkspaceAdminAction row) =>
        new(
            row.Id,
            row.SourceService,
            row.Action,
            row.EntityType,
            row.EntityId,
            row.WorkspaceId,
            row.PerformedBy,
            row.Reason,
            row.Result,
            DateTime.SpecifyKind(row.PerformedAt, DateTimeKind.Utc),
            row.CorrelationId,
            Deserialize(row.BeforeSummary),
            Deserialize(row.AfterSummary));

    private static IReadOnlyDictionary<string, string?>? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
            return AdminAuditRedaction.Redact(parsed);
        }
        catch (JsonException)
        {
            // A malformed summary must not take down the whole page of audit entries.
            return null;
        }
    }
}
