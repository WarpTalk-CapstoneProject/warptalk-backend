using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;
using WarpTalk.Shared.PlatformSettings;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests.PlatformSettings;

/// <summary>
/// The console's write path: server-side validation against the registry, the security permission,
/// risky-needs-a-reason, optimistic concurrency, scopes, history + audit in the same save, revert,
/// export without sensitive values, all-or-nothing import, and a publish after every change.
/// </summary>
public sealed class PlatformSettingsAdminServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Actor = Guid.NewGuid();

    private readonly FakeValues _values = new();
    private readonly FakeChanges _changes = new();
    private readonly List<WorkspaceAdminAction> _audit = new();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IStaffAccessResolver _access = Substitute.For<IStaffAccessResolver>();
    private readonly FakePublisher _publisher = new();
    private readonly HashSet<string> _granted = new(StringComparer.Ordinal) { AdminPermissions.SettingsRead, AdminPermissions.SettingsManage };
    private readonly PlatformSettingsAdminService _service;
    private readonly ClaimsPrincipal _user = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, Actor.ToString()), new Claim(ClaimTypes.Email, "ops@warptalk.vn")], "test"));

    public PlatformSettingsAdminServiceTests()
    {
        _access.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _granted.Contains(call.ArgAt<string>(1)));
        _access.GetAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ => new StaffAccess(true, "custom", "Custom", false, new HashSet<string>(_granted, StringComparer.Ordinal)));
        var auditLog = Substitute.For<IAdminAuditLogRepository>();
        auditLog.AppendAsync(Arg.Any<WorkspaceAdminAction>(), Arg.Any<CancellationToken>())
            .Returns(call => { _audit.Add(call.ArgAt<WorkspaceAdminAction>(0)); return Task.CompletedTask; });
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ => { _values.Commit(); _changes.Commit(); return 1; });

        _service = new PlatformSettingsAdminService(
            _values, _changes, auditLog, _unitOfWork, _access, _publisher,
            NullLogger<PlatformSettingsAdminService>.Instance, null, new FixedTime(Now));
    }

    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    private Task<Result<PlatformSettingDto>> Set(string key, object value, int expected = 0, string? reason = null, string? scopeType = null, string? scopeId = null)
        => _service.SetAsync(_user, key, new PlatformSettingWriteRequest(J(value), scopeType, scopeId, expected, reason), "corr-1");

    [Fact]
    public async Task A_valid_change_is_stored_versioned_recorded_audited_and_published()
    {
        var result = await Set(PlatformSettingsCatalog.LockoutDurationMinutes, 30, reason: null);
        Assert.False(result.IsSuccess); // security category, no settings.security

        _granted.Add(AdminPermissions.SettingsSecurity);
        result = await Set(PlatformSettingsCatalog.LockoutDurationMinutes, 30);

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(result.Value!.IsSet);
        Assert.Equal(1, result.Value.Version);
        Assert.Equal(30, result.Value.Value!.Value.GetInt32());
        Assert.Equal(Now, result.Value.LastChangedAt);

        var change = Assert.Single(_changes.Committed);
        Assert.Equal(PlatformSettingChangeActions.Set, change.Action);
        Assert.Null(change.OldValueJson);
        Assert.Equal("30", change.NewValueJson);
        Assert.Equal("ops@warptalk.vn", change.ChangedByEmail);

        var audit = Assert.Single(_audit);
        Assert.Equal(AdminAuditEntityTypes.PlatformSetting, audit.EntityType);
        Assert.Equal(AdminAuditPlatformSettingActions.Changed, audit.Action);
        Assert.Equal(PlatformSettingsCatalog.LockoutDurationMinutes, audit.EntityKey);
        Assert.Contains("30", audit.AfterSummary);

        var published = Assert.Single(_publisher.Published);
        Assert.Equal(Now.Ticks, published.Version);
        Assert.Contains(published.Values, v => v.SettingKey == PlatformSettingsCatalog.LockoutDurationMinutes);
    }

    [Fact]
    public async Task Security_keys_need_settings_security_and_others_do_not()
    {
        var denied = await Set(PlatformSettingsCatalog.PasswordMinLength, 10);
        Assert.Equal(ErrorCodes.Forbidden, denied.ErrorCode);

        Assert.True((await Set(PlatformSettingsCatalog.ChunkDurationMs, 5000)).IsSuccess);

        var console = await _service.GetAsync(_user);
        Assert.True(console.Value!.CanManage);
        Assert.False(console.Value.CanManageSecurity);
        Assert.False(console.Value.Settings.Single(s => s.Key == PlatformSettingsCatalog.PasswordMinLength).CanEdit);
        Assert.True(console.Value.Settings.Single(s => s.Key == PlatformSettingsCatalog.ChunkDurationMs).CanEdit);
    }

    [Theory]
    [InlineData(PlatformSettingsCatalog.ChunkDurationMs, 1999)]
    [InlineData(PlatformSettingsCatalog.ChunkDurationMs, "6000")]
    [InlineData(PlatformSettingsCatalog.SupportEmail, "nobody")]
    public async Task Invalid_values_are_refused_server_side(string key, object value)
    {
        var result = await Set(key, value);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Empty(_changes.Committed);
        Assert.Empty(_publisher.Published);
    }

    [Fact]
    public async Task Unknown_keys_are_not_found()
        => Assert.Equal(ErrorCodes.NotFound, (await Set("general.nope", true)).ErrorCode);

    [Fact]
    public async Task Risky_settings_need_a_reason_of_ten_characters()
    {
        Assert.Equal(ErrorCodes.ValidationError, (await Set(PlatformSettingsCatalog.MaintenanceEnabled, true)).ErrorCode);
        Assert.Equal(ErrorCodes.ValidationError, (await Set(PlatformSettingsCatalog.MaintenanceEnabled, true, reason: "short")).ErrorCode);
        var ok = await Set(PlatformSettingsCatalog.MaintenanceEnabled, true, reason: "Release 2026.09.25 cut-over");
        Assert.True(ok.IsSuccess, ok.Error);
        Assert.Equal("Release 2026.09.25 cut-over", _changes.Committed.Single().Reason);
    }

    [Fact]
    public async Task A_stale_version_is_a_conflict_and_an_unchanged_value_is_a_no_op()
    {
        Assert.True((await Set(PlatformSettingsCatalog.ChunkDurationMs, 5000)).IsSuccess);
        Assert.Equal(ErrorCodes.Conflict, (await Set(PlatformSettingsCatalog.ChunkDurationMs, 4000, expected: 0)).ErrorCode);
        var missing = await _service.SetAsync(_user, PlatformSettingsCatalog.ChunkDurationMs,
            new PlatformSettingWriteRequest(J(4000), null, null, null, null), null);
        Assert.Equal(ErrorCodes.ValidationError, missing.ErrorCode);

        var same = await Set(PlatformSettingsCatalog.ChunkDurationMs, 5000, expected: 1);
        Assert.True(same.IsSuccess);
        Assert.Single(_changes.Committed);

        var next = await Set(PlatformSettingsCatalog.ChunkDurationMs, 4000, expected: 1);
        Assert.Equal(2, next.Value!.Version);
    }

    [Fact]
    public async Task Overrides_only_at_scopes_the_definition_allows()
    {
        var workspace = Guid.NewGuid();
        Assert.Equal(ErrorCodes.ValidationError,
            (await Set(PlatformSettingsCatalog.ChunkDurationMs, 5000, scopeType: "workspace", scopeId: workspace.ToString())).ErrorCode);
        Assert.Equal(ErrorCodes.ValidationError,
            (await Set(PlatformSettingsCatalog.DocumentUploadMb, 50, scopeType: "workspace", scopeId: "not-a-guid")).ErrorCode);
        Assert.Equal(ErrorCodes.ValidationError,
            (await Set(PlatformSettingsCatalog.DocumentUploadMb, 50, scopeType: "plan", scopeId: "Pro Plan")).ErrorCode);

        var ws = await Set(PlatformSettingsCatalog.DocumentUploadMb, 50, scopeType: "workspace", scopeId: workspace.ToString().ToUpperInvariant());
        Assert.True(ws.IsSuccess, ws.Error);
        var plan = await Set(PlatformSettingsCatalog.DocumentUploadMb, 25, scopeType: "plan", scopeId: "PRO");
        Assert.True(plan.IsSuccess, plan.Error);

        Assert.False(plan.Value!.IsSet);
        Assert.Collection(plan.Value.Overrides.OrderBy(o => o.ScopeType),
            o => { Assert.Equal("plan", o.ScopeType); Assert.Equal("pro", o.ScopeId); },
            o => { Assert.Equal("workspace", o.ScopeType); Assert.Equal(workspace.ToString("D"), o.ScopeId); });
        Assert.Equal(workspace, _audit.Last(a => a.Action == AdminAuditPlatformSettingActions.Changed && a.WorkspaceId is not null).WorkspaceId);
    }

    [Fact]
    public async Task Reset_removes_the_value_and_revert_restores_the_one_a_change_replaced()
    {
        await Set(PlatformSettingsCatalog.ChunkDurationMs, 5000);
        await Set(PlatformSettingsCatalog.ChunkDurationMs, 4000, expected: 1);

        var replaced = _changes.Committed.Last();
        var reverted = await _service.RevertAsync(_user, replaced.Id, new PlatformSettingRevertRequest(null), null);
        Assert.True(reverted.IsSuccess, reverted.Error);
        Assert.Equal(5000, reverted.Value!.Value!.Value.GetInt32());
        var revert = _changes.Committed.Last();
        Assert.Equal(PlatformSettingChangeActions.Revert, revert.Action);
        Assert.Equal(replaced.Id, revert.RevertOf);

        var reset = await _service.ResetAsync(_user, PlatformSettingsCatalog.ChunkDurationMs, new PlatformSettingResetRequest(null, null, 3, null), null);
        Assert.True(reset.IsSuccess, reset.Error);
        Assert.False(reset.Value!.IsSet);
        Assert.Null(reset.Value.Value);
        Assert.Equal(PlatformSettingChangeActions.Reset, _changes.Committed.Last().Action);
        Assert.Empty(_values.Committed);

        // Reverting the very first change (nothing was set before it) resets again.
        var first = _changes.Committed.First();
        Assert.True((await Set(PlatformSettingsCatalog.ChunkDurationMs, 7000)).IsSuccess);
        var back = await _service.RevertAsync(_user, first.Id, new PlatformSettingRevertRequest("Back to deploy default."), null);
        Assert.False(back.Value!.IsSet);
        Assert.Equal(6, _publisher.Published.Count); // set, set, revert, reset, set, revert — each published
    }

    [Fact]
    public async Task Sensitive_values_are_redacted_in_history_and_left_out_of_exports()
    {
        await Set(PlatformSettingsCatalog.MaintenanceAllowlist, new[] { "ops@warptalk.vn" });
        await Set(PlatformSettingsCatalog.ChunkDurationMs, 5000);

        var history = await _service.GetHistoryAsync(PlatformSettingsCatalog.MaintenanceAllowlist, 10);
        var entry = Assert.Single(history.Value!);
        Assert.True(entry.Redacted);
        Assert.Null(entry.NewValue);
        Assert.DoesNotContain("ops@warptalk.vn", _audit.First().AfterSummary);

        var export = await _service.ExportAsync(_user, null);
        Assert.Equal(PlatformSettingsAdminService.ExportFormat, export.Value!.Format);
        var line = Assert.Single(export.Value.Settings);
        Assert.Equal(PlatformSettingsCatalog.ChunkDurationMs, line.Key);
        Assert.Contains(PlatformSettingsCatalog.MaintenanceAllowlist, export.Value.Excluded);
        Assert.Contains(_audit, a => a.Action == AdminAuditPlatformSettingActions.Exported);
    }

    [Fact]
    public async Task Import_plans_on_dry_run_refuses_everything_on_one_bad_line_and_applies_atomically()
    {
        await Set(PlatformSettingsCatalog.ChunkDurationMs, 5000);
        var good = new List<PlatformSettingsExportEntryDto>
        {
            new(PlatformSettingsCatalog.ChunkDurationMs, "platform", "", J(5000)),
            new(PlatformSettingsCatalog.SuggestMinWords, "platform", "", J(6)),
            new(PlatformSettingsCatalog.DocumentUploadMb, "plan", "pro", J(40)),
        };

        var plan = await _service.ImportAsync(_user, new PlatformSettingsImportRequest(good, true, null), null);
        Assert.False(plan.Value!.Applied);
        Assert.Equal(new[] { "unchanged", "create", "create" }, plan.Value.Lines.Select(l => l.Outcome));
        Assert.Single(_changes.Committed);

        var bad = good.Append(new(PlatformSettingsCatalog.PasswordMinLength, "platform", "", J(12))).ToList(); // needs settings.security
        var refused = await _service.ImportAsync(_user, new PlatformSettingsImportRequest(bad, false, "Copy staging settings to prod"), null);
        Assert.False(refused.Value!.Applied);
        Assert.Equal(1, refused.Value.Rejected);
        Assert.Single(_changes.Committed);

        Assert.Equal(ErrorCodes.ValidationError,
            (await _service.ImportAsync(_user, new PlatformSettingsImportRequest(good, false, null), null)).ErrorCode);

        var applied = await _service.ImportAsync(_user, new PlatformSettingsImportRequest(good, false, "Copy staging settings to prod"), null);
        Assert.True(applied.Value!.Applied);
        Assert.Equal(2, applied.Value.Changed);
        Assert.Equal(3, _changes.Committed.Count);
        Assert.All(_changes.Committed.Skip(1), c => Assert.Equal(PlatformSettingChangeActions.Import, c.Action));
        Assert.Single(_audit, a => a.Action == AdminAuditPlatformSettingActions.Imported);
    }

    [Fact]
    public async Task The_console_lists_every_registry_entry_with_categories_and_publish_state()
    {
        await Set(PlatformSettingsCatalog.ChunkDurationMs, 5000);
        var console = (await _service.GetAsync(_user)).Value!;

        Assert.Equal(PlatformSettingsCatalog.All.Count, console.Settings.Count);
        Assert.Equal(SettingCategories.Ordered, console.Categories.Select(c => c.Key));
        Assert.Equal(1, console.Categories.Single(c => c.Key == SettingCategories.Meetings).ChangedCount);
        Assert.True(console.Publish.Healthy);
        var chunk = console.Settings.Single(s => s.Key == PlatformSettingsCatalog.ChunkDurationMs);
        Assert.Equal("integer", chunk.Type);
        Assert.Equal("ms", chunk.Unit);
        Assert.Equal(6000, chunk.DefaultValue.GetInt32());
        Assert.Equal(Actor, chunk.LastChangedBy!.Id);
    }

    // ── Fakes ───────────────────────────────────────────────────────────────────────────────

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    /// <summary>Tracks adds/removes/edits and applies them on "commit", like a change tracker.</summary>
    private sealed class FakeValues : IPlatformSettingValueRepository
    {
        public List<PlatformSettingValue> Committed { get; } = new();
        private readonly List<PlatformSettingValue> _added = new();
        private readonly List<PlatformSettingValue> _removed = new();

        public Task<IReadOnlyList<PlatformSettingValue>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlatformSettingValue>>(Committed.Select(Copy).ToList());

        public Task<PlatformSettingValue?> GetAsync(string key, string scopeType, string scopeId, CancellationToken ct = default)
            => Task.FromResult(Committed.FirstOrDefault(v => v.SettingKey == key && v.ScopeType == scopeType && v.ScopeId == scopeId));

        public Task AddAsync(PlatformSettingValue value, CancellationToken ct = default) { _added.Add(value); return Task.CompletedTask; }

        public void Remove(PlatformSettingValue value) => _removed.Add(value);

        public void Commit()
        {
            Committed.AddRange(_added);
            Committed.RemoveAll(_removed.Contains);
            _added.Clear();
            _removed.Clear();
        }

        private static PlatformSettingValue Copy(PlatformSettingValue v) => new()
        {
            SettingKey = v.SettingKey, ScopeType = v.ScopeType, ScopeId = v.ScopeId, ValueJson = v.ValueJson,
            Version = v.Version, CreatedAt = v.CreatedAt, UpdatedAt = v.UpdatedAt, UpdatedBy = v.UpdatedBy,
        };
    }

    private sealed class FakeChanges : IPlatformSettingChangeRepository
    {
        public List<PlatformSettingChange> Committed { get; } = new();
        private readonly List<PlatformSettingChange> _pending = new();

        public Task AppendAsync(PlatformSettingChange change, CancellationToken ct = default) { _pending.Add(change); return Task.CompletedTask; }

        public Task<PlatformSettingChange?> GetAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Committed.FirstOrDefault(c => c.Id == id));

        public Task<IReadOnlyList<PlatformSettingChange>> GetRecentAsync(string? key, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlatformSettingChange>>(
                Committed.Where(c => key is null || c.SettingKey == key).Reverse().Take(limit).ToList());

        public Task<IReadOnlyDictionary<string, PlatformSettingChange>> GetLatestPerKeyAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, PlatformSettingChange>>(
                Committed.GroupBy(c => c.SettingKey).ToDictionary(g => g.Key, g => g.Last()));

        public void Commit()
        {
            Committed.AddRange(_pending);
            _pending.Clear();
        }
    }

    private sealed class FakePublisher : IPlatformSettingsPublisher
    {
        public List<(IReadOnlyList<PlatformSettingValue> Values, long Version)> Published { get; } = new();

        public PlatformSettingsPublishState State { get; private set; } = new(0, null, true, null);

        public Task<bool> PublishAsync(IReadOnlyList<PlatformSettingValue> values, long version, CancellationToken ct = default)
        {
            Published.Add((values, version));
            State = new PlatformSettingsPublishState(version, DateTime.UtcNow, true, null);
            return Task.FromResult(true);
        }
    }
}
