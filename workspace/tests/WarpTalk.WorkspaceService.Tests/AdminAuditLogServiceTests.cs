using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class AdminAuditLogServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly IAdminAuditLogRepository _repository = Substitute.For<IAdminAuditLogRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly AdminAuditLogService _service;

    public AdminAuditLogServiceTests()
    {
        _repository.QueryAsync(Arg.Any<AdminAuditLogFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<WorkspaceAdminAction>(), 0));

        _service = new AdminAuditLogService(
            _repository, _unitOfWork, Substitute.For<ILogger<AdminAuditLogService>>());
    }

    private static WorkspaceAdminAction Row(
        string? beforeJson = null,
        string? afterJson = null,
        string action = "suspend") => new()
    {
        Id = Guid.NewGuid(),
        SourceService = AdminAuditSources.WorkspaceService,
        Action = action,
        EntityType = AdminAuditEntityTypes.Workspace,
        EntityId = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        PerformedBy = Guid.NewGuid(),
        Reason = "Abuse report",
        Result = AdminAuditResults.Succeeded,
        PerformedAt = Now,
        CorrelationId = "trace-1",
        BeforeSummary = beforeJson,
        AfterSummary = afterJson,
    };

    private static AdminActionRecordedEvent Event(
        string result = AdminAuditResults.Succeeded,
        string? correlationId = "trace-1",
        IReadOnlyDictionary<string, string?>? after = null) =>
        new(
            AdminAuditSources.BillingService,
            "publish_rate_version",
            AdminAuditEntityTypes.UsageRate,
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            "Quarterly rate refresh",
            result,
            Now,
            correlationId,
            null,
            after);

    // ── The API cannot mutate history ────────────────────────

    [Fact]
    public void ServiceExposesNoUpdateOrDeleteOperation()
    {
        var mutators = typeof(IAdminAuditLogService)
            .GetMethods()
            .Select(method => method.Name)
            .Where(name =>
                name.Contains("Update", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Remove", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(mutators);
    }

    [Fact]
    public void RepositoryExposesNoUpdateOrDeleteOperation()
    {
        var mutators = typeof(IAdminAuditLogRepository)
            .GetMethods()
            .Select(method => method.Name)
            .Where(name =>
                name.Contains("Update", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Remove", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(mutators);
    }

    // ── Query validation and filters ─────────────────────────

    [Fact]
    public async Task QueryAsync_RejectsAnUnknownResultFilter()
    {
        var result = await _service.QueryAsync(new AdminAuditLogQuery { Result = "partly" });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task QueryAsync_RejectsAnInvertedDateRange()
    {
        var result = await _service.QueryAsync(
            new AdminAuditLogQuery { From = Now, To = Now.AddDays(-1) });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task QueryAsync_RejectsAnExcessiveDateRange()
    {
        var result = await _service.QueryAsync(
            new AdminAuditLogQuery { From = Now.AddYears(-3), To = Now });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task QueryAsync_PassesEveryFilterThroughAndClampsPaging()
    {
        AdminAuditLogFilter? captured = null;
        _repository.QueryAsync(Arg.Do<AdminAuditLogFilter>(f => captured = f), Arg.Any<CancellationToken>())
            .Returns((new List<WorkspaceAdminAction>(), 0));

        var actorId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        await _service.QueryAsync(new AdminAuditLogQuery
        {
            Page = 0,
            PageSize = 5000,
            ActorId = actorId,
            Action = " suspend ",
            EntityType = AdminAuditEntityTypes.Workspace,
            EntityId = entityId,
            WorkspaceId = workspaceId,
            SourceService = AdminAuditSources.WorkspaceService,
            Result = "SUCCEEDED",
        });

        Assert.NotNull(captured);
        Assert.Equal(1, captured!.Page);
        Assert.Equal(100, captured.PageSize);
        Assert.Equal(actorId, captured.ActorId);
        Assert.Equal("suspend", captured.Action);
        Assert.Equal(entityId, captured.EntityId);
        Assert.Equal(workspaceId, captured.WorkspaceId);
        Assert.Equal(AdminAuditResults.Succeeded, captured.Result);
    }

    [Fact]
    public async Task QueryAsync_ReturnsUtcTimestamps()
    {
        _repository.QueryAsync(Arg.Any<AdminAuditLogFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<WorkspaceAdminAction> { Row() }, 1));

        var result = await _service.QueryAsync(new AdminAuditLogQuery());

        var entry = Assert.Single(result.Value!.Items);
        Assert.Equal(DateTimeKind.Utc, entry.PerformedAt.Kind);
    }

    // ── Redaction ────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_RedactsSensitiveKeysOnRead()
    {
        // A row written before a redaction rule existed must still not leak on read.
        var leaked = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["provider"] = "stripe",
            ["apiKey"] = "sk_live_should_never_surface",
            ["webhook_secret"] = "whsec_should_never_surface",
        });
        _repository.QueryAsync(Arg.Any<AdminAuditLogFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<WorkspaceAdminAction> { Row(afterJson: leaked) }, 1));

        var result = await _service.QueryAsync(new AdminAuditLogQuery());

        var after = Assert.Single(result.Value!.Items).AfterSummary!;
        Assert.Equal("stripe", after["provider"]);
        Assert.Equal(AdminAuditRedaction.RedactedPlaceholder, after["apiKey"]);
        Assert.Equal(AdminAuditRedaction.RedactedPlaceholder, after["webhook_secret"]);
    }

    [Fact]
    public async Task QueryAsync_SurvivesAMalformedSummary()
    {
        _repository.QueryAsync(Arg.Any<AdminAuditLogFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<WorkspaceAdminAction> { Row(afterJson: "{not json") }, 1));

        var result = await _service.QueryAsync(new AdminAuditLogQuery());

        Assert.True(result.IsSuccess);
        Assert.Null(Assert.Single(result.Value!.Items).AfterSummary);
    }

    // ── Recording from other services ────────────────────────

    [Fact]
    public async Task RecordAsync_AppendsAndRedactsBeforePersisting()
    {
        WorkspaceAdminAction? appended = null;
        await _repository.AppendAsync(
            Arg.Do<WorkspaceAdminAction>(row => appended = row), Arg.Any<CancellationToken>());

        var result = await _service.RecordAsync(Event(after: new Dictionary<string, string?>
        {
            ["usdPerCredit"] = "0.02",
            ["providerApiKey"] = "sk_live_should_never_persist",
        }));

        Assert.True(result.IsSuccess);
        Assert.NotNull(appended);
        Assert.Equal(AdminAuditSources.BillingService, appended!.SourceService);
        Assert.Equal(AdminAuditEntityTypes.UsageRate, appended.EntityType);

        var after = JsonSerializer.Deserialize<Dictionary<string, string?>>(appended.AfterSummary!)!;
        Assert.Equal("0.02", after["usdPerCredit"]);
        Assert.Equal(AdminAuditRedaction.RedactedPlaceholder, after["providerApiKey"]);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_IsIdempotentOnRedelivery()
    {
        _repository.ExistsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.RecordAsync(Event());

        Assert.True(result.IsSuccess);
        await _repository.DidNotReceive().AppendAsync(
            Arg.Any<WorkspaceAdminAction>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_RecordsFailedAttempts()
    {
        WorkspaceAdminAction? appended = null;
        await _repository.AppendAsync(
            Arg.Do<WorkspaceAdminAction>(row => appended = row), Arg.Any<CancellationToken>());

        await _service.RecordAsync(Event(result: AdminAuditResults.Failed));

        Assert.Equal(AdminAuditResults.Failed, appended!.Result);
    }

    [Fact]
    public async Task RecordAsync_FallsBackToSucceededForAnUnknownResult()
    {
        WorkspaceAdminAction? appended = null;
        await _repository.AppendAsync(
            Arg.Do<WorkspaceAdminAction>(row => appended = row), Arg.Any<CancellationToken>());

        await _service.RecordAsync(Event(result: "exploded"));

        Assert.Equal(AdminAuditResults.Succeeded, appended!.Result);
    }

    [Fact]
    public async Task RecordAsync_RejectsAnEventMissingItsSubject()
    {
        var incomplete = new AdminActionRecordedEvent(
            SourceService: "",
            Action: "",
            EntityType: "",
            EntityId: null,
            WorkspaceId: null,
            ActorId: Guid.NewGuid(),
            Reason: "",
            Result: AdminAuditResults.Succeeded,
            PerformedAt: Now,
            CorrelationId: null,
            BeforeSummary: null,
            AfterSummary: null);

        var result = await _service.RecordAsync(incomplete);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }
}

public class AdminAuditRedactionTests
{
    [Theory]
    [InlineData("apiKey")]
    [InlineData("api_key")]
    [InlineData("provider-secret")]
    [InlineData("WebhookSecret")]
    [InlineData("stripe_client_secret")]
    [InlineData("bankCredential")]
    [InlineData("Authorization")]
    public void SensitiveKeysAreDetected(string key) =>
        Assert.True(AdminAuditRedaction.IsSensitiveKey(key));

    [Theory]
    [InlineData("provider")]
    [InlineData("status")]
    [InlineData("usdPerCredit")]
    [InlineData("displayOrder")]
    public void OrdinaryKeysAreKept(string key) =>
        Assert.False(AdminAuditRedaction.IsSensitiveKey(key));

    [Fact]
    public void RedactReplacesOnlySensitiveValues()
    {
        var redacted = AdminAuditRedaction.Redact(new Dictionary<string, string?>
        {
            ["provider"] = "stripe",
            ["secret"] = "shh",
        })!;

        Assert.Equal("stripe", redacted["provider"]);
        Assert.Equal(AdminAuditRedaction.RedactedPlaceholder, redacted["secret"]);
    }

    [Fact]
    public void RedactHandlesNull() => Assert.Null(AdminAuditRedaction.Redact(null));
}
