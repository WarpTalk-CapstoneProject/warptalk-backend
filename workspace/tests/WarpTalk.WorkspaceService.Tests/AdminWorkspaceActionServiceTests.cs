using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using Xunit;
using NotificationClient = WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient;
using NotificationResponse = WarpTalk.Shared.Protos.SendNotificationResponse;
using SendNotificationRequest = WarpTalk.Shared.Protos.SendNotificationRequest;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// The workspace-service half of the admin workspace page: notice to the owner, notes, the
/// timeline and the export. Every write lands in the platform audit log, and the notice is
/// recorded before it is sent — a delivered notice must never be the one thing without a record.
/// </summary>
public class AdminWorkspaceActionServiceTests
{
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _actorId = Guid.NewGuid();

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IWorkspaceRepository _workspaces = Substitute.For<IWorkspaceRepository>();
    private readonly IAdminAuditLogRepository _auditLog = Substitute.For<IAdminAuditLogRepository>();
    private readonly IWorkspaceAdminNoteRepository _notes = Substitute.For<IWorkspaceAdminNoteRepository>();
    private readonly IAuthIdentityClient _auth = Substitute.For<IAuthIdentityClient>();
    private readonly IAdminWorkspaceService _adminWorkspaces = Substitute.For<IAdminWorkspaceService>();
    private readonly IWorkspaceMemberService _members = Substitute.For<IWorkspaceMemberService>();
    private readonly RecordingNotificationClient _bell = new();

    /// <summary>"append:succeeded", "save", "send", "append:failed" — in the order they happened.</summary>
    private readonly List<string> _calls = new();

    public AdminWorkspaceActionServiceTests()
    {
        _unitOfWork.WorkspaceRepository.Returns(_workspaces);
        _workspaces.GetByIdAsync(_workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace { Id = _workspaceId, OwnerId = _ownerId, Name = "Acme", Slug = "acme" });
        _auditLog.AppendAsync(Arg.Do<WorkspaceAdminAction>(a => _calls.Add($"append:{a.Result}")), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("save"); return Task.FromResult(1); });
        _bell.OnSend = () => _calls.Add("send");
    }

    private AdminWorkspaceActionService Service(NotificationClient? bell = null) => new(
        _unitOfWork,
        _members,
        _adminWorkspaces,
        _auditLog,
        _notes,
        _auth,
        NullLogger<AdminWorkspaceActionService>.Instance,
        TimeProvider.System,
        bell ?? _bell);

    // ── notice to the owner ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_notice_is_recorded_and_committed_before_it_is_sent_to_the_owner()
    {
        var result = await Service().SendNoticeAsync(
            _workspaceId,
            new AdminWorkspaceNoticeRequest("Invoice overdue", "INV-42 is 10 days overdue.", "Collections follow-up"),
            _actorId,
            "corr-1");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(new[] { "append:succeeded", "save", "send" }, _calls);
        var sent = Assert.Single(_bell.Sent);
        Assert.Equal(_ownerId.ToString(), sent.UserId);
        Assert.Equal(AdminWorkspaceActionService.WorkspaceAdminNoticeNotificationType, sent.Type);
        Assert.Equal("WORKSPACE_ADMIN_NOTICE", sent.Type);
        // Exactly the keys NotificationValidator declares for the type; an undeclared one is refused.
        Assert.Equal(new[] { "workspace_id", "workspace_name" }, sent.Metadata.Keys.OrderBy(k => k));
        await _auditLog.Received(1).AppendAsync(
            Arg.Is<WorkspaceAdminAction>(a =>
                a.Action == AdminAuditWorkspaceActions.NoticeSent
                && a.WorkspaceId == _workspaceId
                && a.Reason == "Collections follow-up"
                && a.PerformedBy == _actorId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_notice_that_could_not_be_delivered_is_followed_by_a_failed_entry()
    {
        _bell.Fail = true;

        var result = await Service().SendNoticeAsync(
            _workspaceId, new AdminWorkspaceNoticeRequest("Title", "Body", "Reason"), _actorId, "corr-1");

        Assert.False(result.IsSuccess);
        Assert.Equal(new[] { "append:succeeded", "save", "append:failed", "save" }, _calls);
        await _auditLog.Received(1).AppendAsync(
            Arg.Is<WorkspaceAdminAction>(a => a.Result == AdminAuditResults.Failed && a.CorrelationId == "corr-1:failed"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("", "Body", "Reason")]
    [InlineData("Title", "", "Reason")]
    [InlineData("Title", "Body", " ")]
    public async Task A_notice_without_a_title_message_or_reason_is_neither_recorded_nor_sent(string title, string message, string reason)
    {
        var result = await Service().SendNoticeAsync(
            _workspaceId, new AdminWorkspaceNoticeRequest(title, message, reason), _actorId, null);

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Empty(_calls);
    }

    // ── notes ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_note_is_stored_and_audited_in_one_save()
    {
        WorkspaceAdminNote? stored = null;
        _notes.AppendAsync(Arg.Do<WorkspaceAdminNote>(n => stored = n), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await Service().AddNoteAsync(
            _workspaceId, new AdminAddWorkspaceNoteRequest("  Called the owner; payment promised Friday.  "), _actorId, "c");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("Called the owner; payment promised Friday.", stored!.Body);
        Assert.Equal(_actorId, stored.AuthorId);
        Assert.Equal(new[] { "append:succeeded", "save" }, _calls);
        await _auditLog.Received(1).AppendAsync(
            Arg.Is<WorkspaceAdminAction>(a => a.Action == AdminAuditWorkspaceActions.NoteAdded && a.EntityId == stored.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_empty_note_is_refused()
    {
        var result = await Service().AddNoteAsync(_workspaceId, new AdminAddWorkspaceNoteRequest("   "), _actorId, null);

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Empty(_calls);
    }

    // ── timeline ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_timeline_merges_every_services_actions_with_the_notes_newest_first()
    {
        var t0 = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
        _auditLog.QueryAsync(Arg.Is<AdminAuditLogFilter>(f => f.WorkspaceId == _workspaceId), Arg.Any<CancellationToken>())
            .Returns((new List<WorkspaceAdminAction>
            {
                new()
                {
                    Id = Guid.NewGuid(), Action = AdminAuditWorkspaceActions.CreditAdjusted, SourceService = AdminAuditSources.BillingService,
                    EntityType = AdminAuditEntityTypes.CreditAdjustment, Reason = "Outage", Result = AdminAuditResults.Succeeded,
                    PerformedBy = _actorId, PerformedAt = t0.AddHours(3), AfterSummary = """{"amount":"250"}""",
                },
                new()
                {
                    Id = Guid.NewGuid(), Action = "suspend", SourceService = AdminAuditSources.WorkspaceService,
                    EntityType = AdminAuditEntityTypes.Workspace, Reason = "Fraud check", Result = AdminAuditResults.Succeeded,
                    PerformedBy = _actorId, PerformedAt = t0.AddHours(1),
                },
            }, 2));
        _notes.GetForWorkspaceAsync(_workspaceId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceAdminNote>
            {
                new() { Id = Guid.NewGuid(), WorkspaceId = _workspaceId, Body = "Owner called", AuthorId = _actorId, CreatedAt = t0.AddHours(2) },
            });
        _auth.GetUserByIdAsync(_actorId, Arg.Any<CancellationToken>()).Returns(new User { Id = _actorId, FullName = "Ops Admin" });

        var result = await Service().GetTimelineAsync(_workspaceId, null);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(new[] { "action", "note", "action" }, result.Value!.Select(e => e.Kind));
        Assert.Equal(new[] { "Outage", "Owner called", "Fraud check" }, result.Value.Select(e => e.Text));
        Assert.Equal("250", result.Value[0].After!["amount"]);
        Assert.All(result.Value, e => Assert.Equal("Ops Admin", e.ActorName));
    }

    // ── export ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_export_is_recorded_with_its_reason_before_it_is_returned()
    {
        _adminWorkspaces.GetDetailAsync(_workspaceId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(DetailOf(_workspaceId)));
        _adminWorkspaces.GetMembersAsync(_workspaceId, Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<AdminWorkspaceMemberDto>>(new List<AdminWorkspaceMemberDto>()));
        _auditLog.QueryAsync(Arg.Any<AdminAuditLogFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<WorkspaceAdminAction>(), 0));
        _notes.GetForWorkspaceAsync(_workspaceId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceAdminNote>());

        var result = await Service().ExportAsync(_workspaceId, new AdminWorkspaceExportRequest("Customer asked for their data"), _actorId, "c");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(new[] { "append:succeeded", "save" }, _calls);
        await _auditLog.Received(1).AppendAsync(
            Arg.Is<WorkspaceAdminAction>(a => a.Action == AdminAuditWorkspaceActions.DataExported && a.Reason == "Customer asked for their data"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_export_without_a_reason_is_refused()
    {
        var result = await Service().ExportAsync(_workspaceId, new AdminWorkspaceExportRequest(""), _actorId, null);

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Empty(_calls);
    }

    [Fact]
    public void A_failed_correlation_id_stays_inside_the_column()
    {
        var id = AdminWorkspaceActionService.FailedCorrelation(new string('x', 100));
        Assert.Equal(100, id!.Length);
        Assert.EndsWith(":failed", id);
    }

    private static AdminWorkspaceDetailDto DetailOf(Guid id) => new(
        id, "Acme", "acme", null, "active",
        new AdminWorkspaceOwnerDto(Guid.NewGuid(), "Owner", "owner@acme.com", null, true),
        1, 1, 0, 0, 0, 0, false, false,
        DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, null, null,
        new List<AdminWorkspaceLifecycleEventDto>());

    private sealed class RecordingNotificationClient : NotificationClient
    {
        public readonly List<SendNotificationRequest> Sent = new();
        public bool Fail;
        public Action? OnSend;

        public override AsyncUnaryCall<NotificationResponse> SendNotificationAsync(
            SendNotificationRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            if (Fail) throw new RpcException(new Status(StatusCode.Unavailable, "mesh down"));
            OnSend?.Invoke();
            Sent.Add(request);
            return new AsyncUnaryCall<NotificationResponse>(
                Task.FromResult(new NotificationResponse { Success = true, NotificationId = "n-1" }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }
    }
}
