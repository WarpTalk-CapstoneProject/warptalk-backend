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
using WarpTalk.WorkspaceService.Application.Models;
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
    private readonly IAuthIdentityClient _authIdentity = Substitute.For<IAuthIdentityClient>();
    private readonly AdminAuditLogService _service;

    public AdminAuditLogServiceTests()
    {
        _repository.SearchAsync(Arg.Any<AdminAuditLogSearch>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceAdminAction>());
        _repository.GetWorkspaceNamesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, AdminAuditWorkspaceName>());
        _authIdentity.GetUserByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((User?)null);

        _service = new AdminAuditLogService(
            _repository, _unitOfWork, Substitute.For<ILogger<AdminAuditLogService>>(), _authIdentity);
    }

    private static WorkspaceAdminAction Row(
        string? beforeJson = null,
        string? afterJson = null,
        string action = "suspend",
        DateTime? at = null) => new()
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
        PerformedAt = at ?? Now,
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

    // ── Query validation ─────────────────────────────────────

    [Theory]
    [InlineData("partly")]
    [InlineData("ok")]
    public async Task SearchAsync_RejectsAnUnknownResultFilter(string value)
    {
        var result = await _service.SearchAsync(new AdminAuditLogQuery { Result = value });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task SearchAsync_RejectsAnInvertedDateRange()
    {
        var result = await _service.SearchAsync(new AdminAuditLogQuery { From = Now, To = Now.AddDays(-1) });

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task SearchAsync_RejectsAnExcessiveDateRange()
    {
        var result = await _service.SearchAsync(new AdminAuditLogQuery { From = Now.AddYears(-3), To = Now });

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task SearchAsync_RejectsAnOverlongSearch()
    {
        var result = await _service.SearchAsync(new AdminAuditLogQuery { Q = new string('x', 201) });

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("MTIzOm5vdC1hLWd1aWQ")]
    public async Task SearchAsync_RejectsAForgedCursor(string cursor)
    {
        var result = await _service.SearchAsync(new AdminAuditLogQuery { Cursor = cursor });

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        await _repository.DidNotReceive().SearchAsync(Arg.Any<AdminAuditLogSearch>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Cursor_RoundTripsTheLastRowExactly()
    {
        var id = Guid.NewGuid();
        var at = new DateTime(2026, 9, 24, 8, 30, 12, DateTimeKind.Utc).AddTicks(1234560);

        Assert.True(AdminAuditLogService.TryDecodeCursor(AdminAuditLogService.EncodeCursor(at, id), out var decodedAt, out var decodedId));

        Assert.Equal(at, decodedAt);
        Assert.Equal(DateTimeKind.Utc, decodedAt.Kind);
        Assert.Equal(id, decodedId);
    }

    [Fact]
    public async Task SearchAsync_PassesEveryFilterToTheDatabase()
    {
        AdminAuditLogSearch? captured = null;
        _repository.SearchAsync(Arg.Do<AdminAuditLogSearch>(s => captured = s), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceAdminAction>());

        var actorId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var cursorId = Guid.NewGuid();

        await _service.SearchAsync(new AdminAuditLogQuery
        {
            Limit = 5000,
            Cursor = AdminAuditLogService.EncodeCursor(Now, cursorId),
            ActorId = actorId,
            Action = " suspend ",
            EntityType = AdminAuditEntityTypes.SupportedLanguage,
            EntityId = " vi ",
            WorkspaceId = workspaceId,
            SourceService = AdminAuditSources.WorkspaceService,
            Result = "FAILED",
            Q = "  spam  ",
        });

        Assert.NotNull(captured);
        Assert.Equal(AdminAuditLogService.MaxLimit + 1, captured!.Take);
        Assert.Equal(Now, captured.AfterPerformedAt);
        Assert.Equal(cursorId, captured.AfterId);
        Assert.Equal(actorId, captured.ActorId);
        Assert.Equal("suspend", captured.Action);
        Assert.Null(captured.EntityId);
        Assert.Equal("vi", captured.EntityKey);
        Assert.Equal(workspaceId, captured.WorkspaceId);
        Assert.Equal(AdminAuditResults.Failed, captured.Result);
        Assert.Equal("spam", captured.Text);
    }

    [Fact]
    public async Task SearchAsync_AGuidSubjectFiltersTheEntityIdNotTheKey()
    {
        AdminAuditLogSearch? captured = null;
        _repository.SearchAsync(Arg.Do<AdminAuditLogSearch>(s => captured = s), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceAdminAction>());
        var entityId = Guid.NewGuid();

        await _service.SearchAsync(new AdminAuditLogQuery { EntityId = entityId.ToString() });

        Assert.Equal(entityId, captured!.EntityId);
        Assert.Null(captured.EntityKey);
    }

    [Fact]
    public async Task SearchAsync_ReturnsACursorOnlyWhenAnotherPageExists()
    {
        var rows = Enumerable.Range(0, 3).Select(i => Row(at: Now.AddMinutes(-i))).ToList();
        _repository.SearchAsync(Arg.Any<AdminAuditLogSearch>(), Arg.Any<CancellationToken>()).Returns(rows);

        var page = (await _service.SearchAsync(new AdminAuditLogQuery { Limit = 2 })).Value!;

        Assert.Equal(2, page.Items.Count);
        Assert.True(page.HasMore);
        Assert.True(AdminAuditLogService.TryDecodeCursor(page.NextCursor!, out var at, out var id));
        Assert.Equal(rows[1].Id, id);
        Assert.Equal(rows[1].PerformedAt, at);

        _repository.SearchAsync(Arg.Any<AdminAuditLogSearch>(), Arg.Any<CancellationToken>()).Returns(rows.Take(2).ToList());
        var last = (await _service.SearchAsync(new AdminAuditLogQuery { Limit = 2 })).Value!;
        Assert.False(last.HasMore);
        Assert.Null(last.NextCursor);
    }

    [Fact]
    public async Task SearchAsync_ReturnsUtcTimestamps()
    {
        _repository.SearchAsync(Arg.Any<AdminAuditLogSearch>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceAdminAction> { Row() });

        var result = await _service.SearchAsync(new AdminAuditLogQuery());

        Assert.Equal(DateTimeKind.Utc, Assert.Single(result.Value!.Items).PerformedAt.Kind);
    }

    // ── Naming what the rows only identify ───────────────────

    [Fact]
    public async Task SearchAsync_NamesAnActorTheRowDidNotRecord()
    {
        var row = Row();
        _repository.SearchAsync(Arg.Any<AdminAuditLogSearch>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceAdminAction> { row });
        _authIdentity.GetUserByIdAsync(row.PerformedBy, Arg.Any<CancellationToken>())
            .Returns(new User { Id = row.PerformedBy, Email = "ops@warptalk.io.vn", FullName = "Ops Admin" });

        var entry = Assert.Single((await _service.SearchAsync(new AdminAuditLogQuery())).Value!.Items);

        Assert.Equal(row.PerformedBy, entry.Actor.Id);
        Assert.Equal("Ops Admin", entry.Actor.Name);
        Assert.Equal("ops@warptalk.io.vn", entry.Actor.Email);
    }

    [Fact]
    public async Task SearchAsync_PrefersTheSnapshotTakenWhenTheActionHappened()
    {
        var row = Row();
        row.ActorEmail = "old@warptalk.io.vn";
        row.ActorName = "Name Then";
        _repository.SearchAsync(Arg.Any<AdminAuditLogSearch>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceAdminAction> { row });

        var entry = Assert.Single((await _service.SearchAsync(new AdminAuditLogQuery())).Value!.Items);

        Assert.Equal("old@warptalk.io.vn", entry.Actor.Email);
        Assert.Equal("Name Then", entry.Actor.Name);
        await _authIdentity.DidNotReceive().GetUserByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_LeavesAnUnknownActorUnnamedRatherThanInventingOne()
    {
        _repository.SearchAsync(Arg.Any<AdminAuditLogSearch>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceAdminAction> { Row() });

        var entry = Assert.Single((await _service.SearchAsync(new AdminAuditLogQuery())).Value!.Items);

        Assert.Null(entry.Actor.Name);
        Assert.Null(entry.Actor.Email);
    }

    [Fact]
    public async Task SearchAsync_NamesWorkspacesAndAccounts()
    {
        var workspaceRow = Row();
        var userRow = Row(action: AdminAuditUserActions.Deactivated);
        userRow.EntityType = AdminAuditEntityTypes.User;
        userRow.WorkspaceId = null;
        var invoiceRow = Row(action: AdminAuditWorkspaceActions.InvoiceMarkedPaid,
            afterJson: """{"status":"paid","invoice_number":"INV-2026-0042"}""");
        invoiceRow.EntityType = AdminAuditEntityTypes.Invoice;
        invoiceRow.WorkspaceId = workspaceRow.WorkspaceId;

        _repository.SearchAsync(Arg.Any<AdminAuditLogSearch>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceAdminAction> { workspaceRow, userRow, invoiceRow });
        _repository.GetWorkspaceNamesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, AdminAuditWorkspaceName>
            {
                [workspaceRow.WorkspaceId!.Value] = new(workspaceRow.WorkspaceId.Value, "Acme", "acme"),
                [workspaceRow.EntityId!.Value] = new(workspaceRow.EntityId.Value, "Acme", "acme"),
            });
        _authIdentity.GetUserByIdAsync(userRow.EntityId!.Value, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userRow.EntityId.Value, Email = "spammer@example.com", FullName = "Spam Account" });

        var items = (await _service.SearchAsync(new AdminAuditLogQuery())).Value!.Items;

        Assert.Equal("Acme", items[0].Entity.Label);
        Assert.Equal("acme", items[0].Entity.WorkspaceSlug);
        Assert.Equal("Spam Account", items[1].Entity.Label);
        Assert.Equal("INV-2026-0042", items[2].Entity.Label);
        Assert.Equal("Acme", items[2].Entity.WorkspaceName);
    }

    [Fact]
    public async Task SearchAsync_DoesNotPresentThePlaceholderReasonAsSomethingSaid()
    {
        var row = Row();
        row.Reason = AdminAuditLogService.NoReasonPlaceholder;
        _repository.SearchAsync(Arg.Any<AdminAuditLogSearch>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceAdminAction> { row });

        var entry = Assert.Single((await _service.SearchAsync(new AdminAuditLogQuery())).Value!.Items);

        Assert.Null(entry.Reason);
    }

    [Fact]
    public async Task GetAsync_ReturnsNotFoundForAnUnknownId()
    {
        var result = await _service.GetAsync(Guid.NewGuid());

        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task GetFacetsAsync_NamesActorsFromTheDirectoryWhenNoSnapshotExists()
    {
        var known = Guid.NewGuid();
        var legacy = Guid.NewGuid();
        _repository.GetFacetsAsync(Arg.Any<CancellationToken>()).Returns(new AdminAuditLogFacetRows(
            [new AdminAuditFacetCount("suspend", 3)],
            [new AdminAuditFacetCount(AdminAuditEntityTypes.Workspace, 3)],
            [new AdminAuditFacetCount(AdminAuditSources.WorkspaceService, 3)],
            [new AdminAuditActorCount(known, 2, "root@warptalk.io.vn", "Root"), new AdminAuditActorCount(legacy, 1, null, null)]));
        _authIdentity.GetUserByIdAsync(legacy, Arg.Any<CancellationToken>())
            .Returns(new User { Id = legacy, Email = "legacy@warptalk.io.vn", FullName = "Legacy Admin" });

        var facets = (await _service.GetFacetsAsync()).Value!;

        Assert.Equal("Root", facets.Actors[0].Name);
        Assert.Equal("Legacy Admin", facets.Actors[1].Name);
        Assert.Equal("legacy@warptalk.io.vn", facets.Actors[1].Email);
        await _authIdentity.DidNotReceive().GetUserByIdAsync(known, Arg.Any<CancellationToken>());
    }

    // ── Export ───────────────────────────────────────────────

    [Fact]
    public async Task ExportCsvAsync_RecordsTheExportBeforeReturningTheFile()
    {
        _repository.SearchAsync(Arg.Any<AdminAuditLogSearch>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceAdminAction> { Row() });
        WorkspaceAdminAction? appended = null;
        await _repository.AppendAsync(Arg.Do<WorkspaceAdminAction>(row => appended = row), Arg.Any<CancellationToken>());
        var actor = new AdminActorContext(Guid.NewGuid(), "trace-export");

        var export = (await _service.ExportCsvAsync(new AdminAuditLogQuery { Result = "failed" }, actor)).Value!;

        Assert.Equal(1, export.RowCount);
        Assert.False(export.Truncated);
        Assert.EndsWith(".csv", export.FileName);
        Assert.NotNull(appended);
        Assert.Equal(AdminAuditLogActions.Exported, appended!.Action);
        Assert.Equal(AdminAuditEntityTypes.AuditLog, appended.EntityType);
        Assert.Equal(actor.ActorId, appended.PerformedBy);
        Assert.Equal("trace-export", appended.CorrelationId);
        var summary = JsonSerializer.Deserialize<Dictionary<string, string?>>(appended.AfterSummary!)!;
        Assert.Equal("failed", summary["result"]);
        Assert.Equal("1", summary["rows"]);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportCsvAsync_RefusesTheFileWhenTheExportCannotBeRecorded()
    {
        _repository.SearchAsync(Arg.Any<AdminAuditLogSearch>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceAdminAction> { Row() });
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("database down"));

        var result = await _service.ExportCsvAsync(new AdminAuditLogQuery(), new AdminActorContext(Guid.NewGuid(), "t"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InternalServerError, result.ErrorCode);
    }

    [Fact]
    public async Task ExportCsvAsync_WalksEveryPageWithTheCursor()
    {
        var first = Enumerable.Range(0, 500).Select(i => Row(at: Now.AddSeconds(-i))).ToList();
        var second = new List<WorkspaceAdminAction> { Row(at: Now.AddHours(-1)) };
        var calls = new List<AdminAuditLogSearch>();
        _repository.SearchAsync(Arg.Do<AdminAuditLogSearch>(calls.Add), Arg.Any<CancellationToken>())
            .Returns(first, second);

        var export = (await _service.ExportCsvAsync(new AdminAuditLogQuery(), new AdminActorContext(Guid.NewGuid(), "t"))).Value!;

        Assert.Equal(501, export.RowCount);
        Assert.Equal(2, calls.Count);
        Assert.Null(calls[0].AfterId);
        Assert.Equal(first[^1].Id, calls[1].AfterId);
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
    public async Task RecordAsync_KeepsTheRequestContextTheCallerSent()
    {
        WorkspaceAdminAction? appended = null;
        await _repository.AppendAsync(
            Arg.Do<WorkspaceAdminAction>(row => appended = row), Arg.Any<CancellationToken>());

        await _service.RecordAsync(Event(result: AdminAuditResults.Failed) with
        {
            ActorEmail = "root@warptalk.io.vn",
            ActorName = "Root",
            EntityKey = "vi",
            EntityLabel = "Vietnamese",
            ErrorMessage = "Rate overlaps an active card",
            IpAddress = "203.0.113.7",
            UserAgent = new string('a', 900),
        });

        Assert.Equal("root@warptalk.io.vn", appended!.ActorEmail);
        Assert.Equal("Root", appended.ActorName);
        Assert.Equal("vi", appended.EntityKey);
        Assert.Equal("Vietnamese", appended.EntityLabel);
        Assert.Equal("Rate overlaps an active card", appended.ErrorMessage);
        Assert.Equal("203.0.113.7", appended.IpAddress);
        Assert.Equal(512, appended.UserAgent!.Length);
    }

    [Fact]
    public async Task RecordAsync_DropsAnErrorFromASucceededEntry()
    {
        WorkspaceAdminAction? appended = null;
        await _repository.AppendAsync(
            Arg.Do<WorkspaceAdminAction>(row => appended = row), Arg.Any<CancellationToken>());

        await _service.RecordAsync(Event() with { ErrorMessage = "stale text" });

        Assert.Null(appended!.ErrorMessage);
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
