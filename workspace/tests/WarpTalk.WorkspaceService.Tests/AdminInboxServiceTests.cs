using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// G12 pending-work inbox: sources the caller cannot see are never read, a source that fails degrades
/// alone, triage is only possible on items the caller can see, and "done" is refused where the source
/// closes the item itself.
/// </summary>
public sealed class AdminInboxServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 3, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Viewer = Guid.NewGuid();

    private readonly FakeSources _sources = new();
    private readonly IStaffAccessResolver _access = Substitute.For<IStaffAccessResolver>();
    private readonly FakeStates _states = new();
    private readonly FakeNotes _notes = new();
    private readonly IAdminAuditLogRepository _audit = Substitute.For<IAdminAuditLogRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly HashSet<string> _granted = new(StringComparer.Ordinal);
    private readonly AdminInboxService _service;
    private readonly ClaimsPrincipal _user = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Viewer.ToString()), new Claim("sub", Viewer.ToString())], "test"));

    public AdminInboxServiceTests()
    {
        _access.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _granted.Contains(call.ArgAt<string>(1)));
        var identity = Substitute.For<IAuthIdentityClient>();
        identity.GetUserByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new WarpTalk.WorkspaceService.Application.Models.User { Id = call.ArgAt<Guid>(0), FullName = "Tú", Email = "tu@warptalk.vn" });

        _service = new AdminInboxService(
            _sources, _access, _states, _notes, _audit, _unitOfWork, identity,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<AdminInboxService>.Instance, new FixedTime(Now));

        _sources.Responses[AdminInbox.Sources.Billing] = Response(AdminInbox.Sources.Billing,
            Item("invoice_past_due:1", AdminInbox.Types.InvoicePastDue, natural: true, due: Now.AddDays(-1), priority: AdminInbox.Priorities.Urgent),
            Item("trial_ending:2:20260926", AdminInbox.Types.TrialEnding, natural: false, due: Now.AddDays(1)));
        _sources.Responses[AdminInbox.Sources.Staff] = Response(AdminInbox.Sources.Staff,
            Item("staff_invitation:3", AdminInbox.Types.StaffInvitation, natural: true, due: Now.AddDays(5), href: "https://evil.example/phish"));
        _sources.Failing.Add(AdminInbox.Sources.Content);
    }

    [Fact]
    public async Task Only_sources_the_role_can_see_are_read_and_a_failing_one_degrades_alone()
    {
        _granted.UnionWith([AdminPermissions.BillingRead, AdminPermissions.ContentAnnouncements]);

        var inbox = (await _service.GetAsync(_user, refresh: false)).Value!;

        Assert.Equal(new[] { AdminInbox.Sources.Billing, AdminInbox.Sources.Content }, _sources.Read.OrderBy(s => s).ToArray());
        Assert.Equal(new[] { "invoice_past_due:1", "trial_ending:2:20260926" }, inbox.Items.Select(i => i.Key).ToArray());
        Assert.Equal(AdminInboxService.SourceStatuses.Unavailable, inbox.Sources.Single(s => s.Source == AdminInbox.Sources.Content).Status);
        Assert.Equal(AdminInboxService.SourceStatuses.Forbidden, inbox.Sources.Single(s => s.Source == AdminInbox.Sources.Staff).Status);
        Assert.True(inbox.Items[0].Overdue);
        Assert.Equal(2, inbox.Counts.Open);
        Assert.Equal(1, inbox.Counts.Overdue);
    }

    [Fact]
    public async Task A_link_outside_the_admin_portal_is_replaced()
    {
        _granted.Add(AdminPermissions.StaffRead);

        var inbox = (await _service.GetAsync(_user, refresh: false)).Value!;

        Assert.Equal("/admin/inbox", inbox.Items.Single().Href);
    }

    [Fact]
    public async Task Triage_assigns_snoozes_and_closes_only_items_without_natural_completion()
    {
        _granted.Add(AdminPermissions.BillingRead);

        var assigned = await _service.AssignAsync(_user, new AdminInboxAssignRequest("trial_ending:2:20260926", Viewer), null);
        Assert.Equal("Tú", assigned.Value!.Triage.AssigneeName);

        var refused = await _service.MarkDoneAsync(_user, new AdminInboxKeyRequest("invoice_past_due:1"), null);
        Assert.Equal(ErrorCodes.Conflict, refused.ErrorCode);

        var done = await _service.MarkDoneAsync(_user, new AdminInboxKeyRequest("trial_ending:2:20260926"), null);
        Assert.True(done.Value!.Done);

        var snoozed = await _service.SnoozeAsync(_user, new AdminInboxSnoozeRequest("invoice_past_due:1", Now.AddDays(2)), null);
        Assert.True(snoozed.Value!.Snoozed);
        Assert.Equal(ErrorCodes.ValidationError,
            (await _service.SnoozeAsync(_user, new AdminInboxSnoozeRequest("invoice_past_due:1", Now.AddDays(-1)), null)).ErrorCode);

        var inbox = (await _service.GetAsync(_user, refresh: false)).Value!;
        Assert.Equal(new AdminInboxCountsDto(0, 0, 0, 0, 1, 1), inbox.Counts);
        await _audit.Received(3).AppendAsync(
            Arg.Is<WorkspaceAdminAction>(a => a.EntityType == AdminAuditEntityTypes.InboxItem && a.EntityKey != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_item_the_caller_cannot_see_cannot_be_triaged_or_annotated()
    {
        _granted.Add(AdminPermissions.StaffRead);

        Assert.Equal(ErrorCodes.NotFound,
            (await _service.SnoozeAsync(_user, new AdminInboxSnoozeRequest("invoice_past_due:1", Now.AddDays(1)), null)).ErrorCode);
        Assert.Equal(ErrorCodes.NotFound,
            (await _service.AddNoteAsync(_user, new AdminInboxNoteRequest("invoice_past_due:1", "called"), null)).ErrorCode);

        var note = await _service.AddNoteAsync(_user, new AdminInboxNoteRequest("staff_invitation:3", "  pinged them  "), null);
        Assert.Equal("pinged them", note.Value!.Body);
        Assert.Equal(1, (await _service.GetAsync(_user, false)).Value!.Items.Single().Triage.NoteCount);
    }

    [Fact]
    public async Task The_badge_summary_reuses_the_cached_read()
    {
        _granted.Add(AdminPermissions.BillingRead);

        await _service.GetAsync(_user, refresh: false);
        var summary = (await _service.GetSummaryAsync(_user)).Value!;

        Assert.Equal(2, summary.Counts.Open);
        Assert.Single(_sources.Read);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────────────────────

    private static AdminInboxItem Item(string key, string type, bool natural, DateTime due, string priority = "normal", string href = "/admin/billing")
        => new(key, type, key, null, null, null, Now.AddDays(-2), due, priority, href, natural);

    private static AdminInboxSourceResponse Response(string source, params AdminInboxItem[] items) => new(source, Now, items);

    private sealed class FakeSources : IAdminInboxSourceClient
    {
        public Dictionary<string, AdminInboxSourceResponse> Responses { get; } = new();
        public HashSet<string> Failing { get; } = new();
        public List<string> Read { get; } = new();

        public IReadOnlyList<AdminInboxSourceDefinition> Sources { get; } =
        [
            new(AdminInbox.Sources.Billing, AdminPermissions.BillingRead),
            new(AdminInbox.Sources.Staff, AdminPermissions.StaffRead),
            new(AdminInbox.Sources.Content, AdminPermissions.ContentAnnouncements),
        ];

        public Task<AdminInboxSourceResult> ReadAsync(string source, CancellationToken ct)
        {
            lock (Read) Read.Add(source);
            if (Failing.Contains(source))
                return Task.FromResult(new AdminInboxSourceResult(source, null, AdminInboxService.SourceStatuses.Unavailable, 5000, "timed out"));
            return Task.FromResult(new AdminInboxSourceResult(source, Responses.GetValueOrDefault(source) ?? Response(source), AdminInboxService.SourceStatuses.Ok, 3, null));
        }
    }

    private sealed class FakeStates : IAdminInboxStateRepository
    {
        private readonly List<AdminInboxItemState> _rows = [];

        public Task<IReadOnlyList<AdminInboxItemState>> GetManyAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AdminInboxItemState>>(_rows.Where(r => keys.Contains(r.ItemKey)).ToList());

        public Task<AdminInboxItemState?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_rows.FirstOrDefault(r => r.ItemKey == key));

        public Task AddAsync(AdminInboxItemState state, CancellationToken ct = default)
        {
            _rows.Add(state);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeNotes : IAdminInboxNoteRepository
    {
        private readonly List<AdminInboxNote> _rows = [];

        public Task AppendAsync(AdminInboxNote note, CancellationToken ct = default)
        {
            _rows.Add(note);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AdminInboxNote>> GetForItemAsync(string key, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AdminInboxNote>>(_rows.Where(r => r.ItemKey == key).Take(limit).ToList());

        public Task<IReadOnlyDictionary<string, (int Count, DateTime LastAt)>> SummarizeAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, (int Count, DateTime LastAt)>>(_rows
                .Where(r => keys.Contains(r.ItemKey))
                .GroupBy(r => r.ItemKey)
                .ToDictionary(g => g.Key, g => (g.Count(), g.Max(n => n.CreatedAt))));
    }

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
