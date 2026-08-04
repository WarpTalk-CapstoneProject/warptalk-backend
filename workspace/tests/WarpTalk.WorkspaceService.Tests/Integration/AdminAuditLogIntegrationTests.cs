using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests.Integration;

/// <summary>
/// Exercises the audit-log query API against real PostgreSQL so the filters, ordering, and
/// paging are proven to translate to SQL (WT-210).
/// </summary>
public class AdminAuditLogIntegrationTests : BaseIntegrationTest
{
    private readonly Guid _adminUserId = Guid.NewGuid();
    private readonly Guid _otherAdminId = Guid.NewGuid();
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _rateId = Guid.NewGuid();

    private HttpClient AdminClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", GenerateJwtToken(_adminUserId, "root@warptalk.io.vn", "admin"));
        return client;
    }

    private async Task SeedAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        var now = DateTime.UtcNow;

        db.WorkspaceAdminActions.AddRange(
            new WorkspaceAdminAction
            {
                Id = Guid.NewGuid(),
                SourceService = AdminAuditSources.WorkspaceService,
                Action = "suspend",
                EntityType = AdminAuditEntityTypes.Workspace,
                EntityId = _workspaceId,
                WorkspaceId = _workspaceId,
                PerformedBy = _adminUserId,
                Reason = "Abuse report",
                Result = AdminAuditResults.Succeeded,
                PerformedAt = now.AddMinutes(-30),
                CorrelationId = "trace-suspend",
            },
            new WorkspaceAdminAction
            {
                Id = Guid.NewGuid(),
                SourceService = AdminAuditSources.WorkspaceService,
                Action = "reactivate",
                EntityType = AdminAuditEntityTypes.Workspace,
                EntityId = _workspaceId,
                WorkspaceId = _workspaceId,
                PerformedBy = _otherAdminId,
                Reason = "Remediated",
                Result = AdminAuditResults.Succeeded,
                PerformedAt = now.AddMinutes(-10),
                CorrelationId = "trace-reactivate",
            },
            // A platform-wide action from another service: no workspace scope at all.
            new WorkspaceAdminAction
            {
                Id = Guid.NewGuid(),
                SourceService = AdminAuditSources.BillingService,
                Action = "publish_rate_version",
                EntityType = AdminAuditEntityTypes.UsageRate,
                EntityId = _rateId,
                WorkspaceId = null,
                PerformedBy = _adminUserId,
                Reason = "Quarterly refresh",
                Result = AdminAuditResults.Failed,
                PerformedAt = now.AddMinutes(-5),
                CorrelationId = "trace-rate",
                AfterSummary = """{"provider":"cartesia","apiKey":"sk_live_leaked"}""",
            });

        await db.SaveChangesAsync();
    }

    private static Task<AdminPagedResult<AdminAuditLogEntryDto>?> Get(HttpClient client, string query) =>
        client.GetFromJsonAsync<AdminPagedResult<AdminAuditLogEntryDto>>($"/api/v1/admin/audit-log{query}");

    [Fact]
    public async Task RejectsUnauthenticatedCallers()
    {
        var response = await Factory.CreateClient().GetAsync("/api/v1/admin/audit-log");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RejectsAuthenticatedNonAdmins()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", GenerateJwtToken(Guid.NewGuid(), "member@acme.com"));

        var response = await client.GetAsync("/api/v1/admin/audit-log");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReturnsNewestFirstAcrossEveryService()
    {
        await SeedAsync();

        var page = await Get(AdminClient(), string.Empty);

        Assert.Equal(3, page!.Total);
        Assert.Equal("publish_rate_version", page.Items[0].Action);
        Assert.Equal("reactivate", page.Items[1].Action);
        Assert.Equal("suspend", page.Items[2].Action);
        Assert.All(page.Items, entry => Assert.Equal(DateTimeKind.Utc, entry.PerformedAt.Kind));
    }

    [Fact]
    public async Task FiltersByActorActionEntityAndResult()
    {
        await SeedAsync();
        var client = AdminClient();

        var byActor = await Get(client, $"?actorId={_otherAdminId}");
        Assert.Equal("reactivate", Assert.Single(byActor!.Items).Action);

        var byAction = await Get(client, "?action=suspend");
        Assert.Equal("Abuse report", Assert.Single(byAction!.Items).Reason);

        var byEntity = await Get(client, $"?entityType={AdminAuditEntityTypes.UsageRate}&entityId={_rateId}");
        Assert.Equal(AdminAuditSources.BillingService, Assert.Single(byEntity!.Items).SourceService);

        var byWorkspace = await Get(client, $"?workspaceId={_workspaceId}");
        Assert.Equal(2, byWorkspace!.Total);

        var failed = await Get(client, "?result=failed");
        Assert.Equal("publish_rate_version", Assert.Single(failed!.Items).Action);

        var rejected = await client.GetAsync("/api/v1/admin/audit-log?result=partly");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task PagesDeterministically()
    {
        await SeedAsync();
        var client = AdminClient();

        var first = await Get(client, "?page=1&pageSize=2");
        var second = await Get(client, "?page=2&pageSize=2");

        Assert.Equal(3, first!.Total);
        Assert.Equal(2, first.Items.Count);
        var only = Assert.Single(second!.Items);
        Assert.DoesNotContain(first.Items, item => item.Id == only.Id);
    }

    [Fact]
    public async Task RedactsSecretsThatReachedTheTableAnyway()
    {
        await SeedAsync();

        var page = await Get(AdminClient(), $"?entityId={_rateId}");

        var after = Assert.Single(page!.Items).AfterSummary!;
        Assert.Equal("cartesia", after["provider"]);
        Assert.Equal("[redacted]", after["apiKey"]);
    }

    [Fact]
    public async Task ExposesNoWriteEndpoints()
    {
        await SeedAsync();
        var client = AdminClient();
        var entryId = (await Get(client, string.Empty))!.Items.First().Id;

        // The controller is query-only, so every mutating verb must be unroutable.
        foreach (var request in new[]
                 {
                     new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/audit-log"),
                     new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/audit-log/{entryId}"),
                     new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/admin/audit-log/{entryId}"),
                     new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/admin/audit-log/{entryId}"),
                 })
        {
            var response = await client.SendAsync(request);
            Assert.True(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"{request.Method} {request.RequestUri} returned {(int)response.StatusCode}");
        }
    }
}
