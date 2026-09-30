using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceAuditLog;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests.Integration;

/// <summary>
/// The workspace-scoped audit log over HTTP and real PostgreSQL: membership authorization,
/// forced workspace scoping, the category allowlist translating to SQL, and actor redaction.
/// </summary>
public class WorkspaceAuditLogIntegrationTests : BaseIntegrationTest
{
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _otherWorkspaceId = Guid.NewGuid();
    private readonly Guid _ownerUserId = Guid.NewGuid();
    private readonly Guid _memberUserId = Guid.NewGuid();
    private readonly Guid _outsiderUserId = Guid.NewGuid();
    private readonly Guid _staffId = Guid.NewGuid();
    private readonly Guid _ownerRoleId = Guid.NewGuid();
    private readonly Guid _memberRoleId = Guid.NewGuid();

    private HttpClient ClientFor(Guid userId, params string[] roles)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", GenerateJwtToken(userId, $"{userId:N}@acme.com", roles));
        return client;
    }

    private string Url(Guid workspaceId, string query = "") =>
        $"/api/v1/workspaces/{workspaceId}/audit-log{query}";

    private async Task SeedAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        var now = DateTime.UtcNow;

        foreach (var (id, slug) in new[] { (_workspaceId, "acme"), (_otherWorkspaceId, "globex") })
        {
            db.Workspaces.Add(new Workspace
            {
                Id = id, Name = slug, Slug = slug, OwnerId = _ownerUserId, Settings = "{}",
                IsActive = true, CreatedAt = now.AddDays(-10), UpdatedAt = now,
            });
        }

        db.WorkspaceMembers.AddRange(
            Member(_workspaceId, _ownerUserId, _ownerRoleId, now),
            Member(_workspaceId, _memberUserId, _memberRoleId, now),
            // The outsider owns a different workspace: owning *a* workspace must not open this one.
            Member(_otherWorkspaceId, _outsiderUserId, _ownerRoleId, now));

        db.WorkspaceAdminActions.AddRange(
            Action(_workspaceId, "suspend", AdminAuditEntityTypes.Workspace, now.AddMinutes(-30)),
            Action(_workspaceId, "reactivate", AdminAuditEntityTypes.Workspace, now.AddMinutes(-10)),
            // A failed staff attempt: internal noise, never shown to the tenant.
            Action(_workspaceId, "delete", AdminAuditEntityTypes.Workspace, now.AddMinutes(-5), AdminAuditResults.Failed),
            // A category not reviewed for tenants, even though it carries this workspace id.
            Action(_workspaceId, AdminAuditUserActions.Deactivated, AdminAuditEntityTypes.User, now.AddMinutes(-4)),
            // Another tenant's history.
            Action(_otherWorkspaceId, "suspend", AdminAuditEntityTypes.Workspace, now.AddMinutes(-3)),
            // Platform-wide.
            Action(null, "publish_rate_version", AdminAuditEntityTypes.UsageRate, now.AddMinutes(-2)));

        await db.SaveChangesAsync();

        MockAuthIdentity.GetRoleByIdAsync(_ownerRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = _ownerRoleId, Name = "Owner" });
        MockAuthIdentity.GetRoleByIdAsync(_memberRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = _memberRoleId, Name = "Member" });
    }

    private static WorkspaceMember Member(Guid workspaceId, Guid userId, Guid roleId, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        UserId = userId,
        RoleId = roleId,
        MembershipType = MembershipType.Internal.ToString(),
        Status = WorkspaceMemberStatus.Active.ToStorageValue(),
        JoinedAt = now.AddDays(-5),
        CanCreateMeetings = true,
    };

    private WorkspaceAdminAction Action(
        Guid? workspaceId, string action, string entityType, DateTime at, string result = AdminAuditResults.Succeeded) => new()
    {
        Id = Guid.NewGuid(),
        SourceService = AdminAuditSources.WorkspaceService,
        Action = action,
        EntityType = entityType,
        EntityId = workspaceId ?? Guid.NewGuid(),
        WorkspaceId = workspaceId,
        PerformedBy = _staffId,
        Reason = "Internal note",
        Result = result,
        PerformedAt = at,
        CorrelationId = $"trace-{Guid.NewGuid():N}",
        AfterSummary = "{\"status\":\"suspended\"}",
    };

    [Fact]
    public async Task Owner_SeesOnlyThisWorkspacesSucceededTenantVisibleEntries_WithTheActorRedacted()
    {
        await SeedAsync();

        var response = await ClientFor(_ownerUserId).GetAsync(Url(_workspaceId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_staffId.ToString(), raw);
        Assert.DoesNotContain("Internal note", raw);
        Assert.DoesNotContain("trace-", raw);

        var page = await response.Content.ReadFromJsonAsync<AdminPagedResult<WorkspaceAuditLogEntryDto>>();
        Assert.NotNull(page);
        Assert.Equal(2, page!.Total);
        Assert.Equal(new[] { "reactivate", "suspend" }, page.Items.Select(i => i.Action).ToArray());
        Assert.All(page.Items, i => Assert.Equal("WarpTalk staff", i.ActorDisplayName));
    }

    [Fact]
    public async Task QueryStringCannotWidenTheScope()
    {
        await SeedAsync();

        // workspaceId and result are not part of the tenant query contract; they must be ignored.
        var page = await ClientFor(_ownerUserId).GetFromJsonAsync<AdminPagedResult<WorkspaceAuditLogEntryDto>>(
            Url(_workspaceId, $"?workspaceId={_otherWorkspaceId}&result=failed&entityType=user&pageSize=50"));

        Assert.NotNull(page);
        Assert.Equal(0, page!.Total);

        var unfiltered = await ClientFor(_ownerUserId).GetFromJsonAsync<AdminPagedResult<WorkspaceAuditLogEntryDto>>(
            Url(_workspaceId, $"?workspaceId={_otherWorkspaceId}&result=failed"));
        Assert.Equal(2, unfiltered!.Total);
    }

    [Fact]
    public async Task ActionAndDateFilters_Apply()
    {
        await SeedAsync();

        var page = await ClientFor(_ownerUserId).GetFromJsonAsync<AdminPagedResult<WorkspaceAuditLogEntryDto>>(
            Url(_workspaceId, "?action=suspend"));
        Assert.Equal("suspend", Assert.Single(page!.Items).Action);

        var from = Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(-20).ToString("O"));
        var recent = await ClientFor(_ownerUserId).GetFromJsonAsync<AdminPagedResult<WorkspaceAuditLogEntryDto>>(
            Url(_workspaceId, $"?from={from}"));
        Assert.Equal("reactivate", Assert.Single(recent!.Items).Action);
    }

    [Fact]
    public async Task PlainMember_Gets403()
    {
        await SeedAsync();
        var response = await ClientFor(_memberUserId).GetAsync(Url(_workspaceId));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task OwnerOfAnotherWorkspace_Gets403()
    {
        await SeedAsync();
        var response = await ClientFor(_outsiderUserId).GetAsync(Url(_workspaceId));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SystemAdminWhoIsNotAMember_Gets403_TheTenantRouteIsNotAStaffBackdoor()
    {
        await SeedAsync();
        var response = await ClientFor(_staffId, "admin").GetAsync(Url(_workspaceId));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_Gets401()
    {
        await SeedAsync();
        var response = await Factory.CreateClient().GetAsync(Url(_workspaceId));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
