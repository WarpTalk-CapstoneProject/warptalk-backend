using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests.Integration;

/// <summary>
/// The platform audit query API against real PostgreSQL, so the filters, the free-text search,
/// the keyset cursor and the superseded-attempt rule are proven to translate to SQL.
/// </summary>
public class AdminAuditLogIntegrationTests : BaseIntegrationTest
{
    private readonly Guid _adminUserId = Guid.NewGuid();
    private readonly Guid _otherAdminId = Guid.NewGuid();
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _rateId = Guid.NewGuid();
    private readonly Guid _planId = Guid.NewGuid();
    private DateTime _now;

    private HttpClient AdminClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", GenerateJwtToken(_adminUserId, "root@warptalk.io.vn", "admin"));
        return client;
    }

    private static WorkspaceAdminAction Row(
        string action,
        DateTime at,
        Guid actor,
        string entityType = AdminAuditEntityTypes.Workspace,
        Guid? entityId = null,
        Guid? workspaceId = null,
        string result = AdminAuditResults.Succeeded,
        string source = AdminAuditSources.WorkspaceService,
        string reason = "Routine",
        string? correlation = null) => new()
    {
        Id = Guid.NewGuid(),
        SourceService = source,
        Action = action,
        EntityType = entityType,
        EntityId = entityId,
        WorkspaceId = workspaceId,
        PerformedBy = actor,
        Reason = reason,
        Result = result,
        PerformedAt = at,
        CorrelationId = correlation ?? Guid.NewGuid().ToString("N"),
    };

    private async Task SeedAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        // Postgres keeps microseconds; the seed does too, so a cursor made from a read row matches.
        _now = new DateTime(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);

        db.Workspaces.Add(new Workspace
        {
            Id = _workspaceId,
            Name = "Acme Translation",
            Slug = "acme-translation",
            OwnerId = Guid.NewGuid(),
            Settings = "{}",
            IsActive = true,
            CreatedAt = _now.AddDays(-30),
            UpdatedAt = _now.AddDays(-1),
        });

        db.WorkspaceAdminActions.AddRange(
            Row("suspend", _now.AddMinutes(-30), _adminUserId, entityId: _workspaceId, workspaceId: _workspaceId,
                reason: "Spam campaign reported", correlation: "trace-suspend"),
            Row("reactivate", _now.AddMinutes(-10), _otherAdminId, entityId: _workspaceId, workspaceId: _workspaceId,
                reason: "Remediated"),
            new WorkspaceAdminAction
            {
                Id = Guid.NewGuid(),
                SourceService = AdminAuditSources.BillingService,
                Action = "publish_rate_version",
                EntityType = AdminAuditEntityTypes.UsageRate,
                EntityId = _rateId,
                PerformedBy = _adminUserId,
                Reason = "Quarterly refresh",
                Result = AdminAuditResults.Failed,
                ErrorMessage = "Rate overlaps an active card",
                PerformedAt = _now.AddMinutes(-5),
                CorrelationId = "trace-rate",
                AfterSummary = """{"provider":"cartesia","apiKey":"sk_live_leaked"}""",
                IpAddress = "203.0.113.7",
                UserAgent = "Mozilla/5.0",
                ActorEmail = "root@warptalk.io.vn",
            },
            new WorkspaceAdminAction
            {
                Id = Guid.NewGuid(),
                SourceService = AdminAuditSources.TranslationRoomService,
                Action = AdminAuditLanguageActions.Disabled,
                EntityType = AdminAuditEntityTypes.SupportedLanguage,
                EntityKey = "vi",
                EntityLabel = "Vietnamese",
                PerformedBy = _adminUserId,
                Reason = "Disabled vi for new rooms",
                Result = AdminAuditResults.Succeeded,
                PerformedAt = _now.AddMinutes(-4),
                CorrelationId = "trace-lang",
            },
            // An attempt recorded before its commit, then the failure of that commit: one account.
            Row("plan.updated", _now.AddMinutes(-3), _adminUserId, AdminAuditEntityTypes.Plan, _planId,
                source: AdminAuditSources.BillingService, correlation: "trace-plan"),
            Row("plan.updated", _now.AddMinutes(-2), _adminUserId, AdminAuditEntityTypes.Plan, _planId,
                result: AdminAuditResults.Failed, source: AdminAuditSources.BillingService,
                correlation: "trace-plan:failed"));

        await db.SaveChangesAsync();

        MockAuthIdentity.GetUserByIdAsync(_otherAdminId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = _otherAdminId, Email = "ops@warptalk.io.vn", FullName = "Ops Admin" });
    }

    private static Task<AdminAuditLogPageDto?> Get(HttpClient client, string query) =>
        client.GetFromJsonAsync<AdminAuditLogPageDto>($"/api/v1/admin/audit-log{query}");

    [Fact]
    public async Task RejectsUnauthenticatedCallers()
    {
        var response = await Factory.CreateClient().GetAsync("/api/v1/admin/audit-log");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RejectsAuthenticatedNonAdminsOnEveryRead()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", GenerateJwtToken(Guid.NewGuid(), "member@acme.com"));

        foreach (var path in new[] { "", "/facets", "/export", $"/{Guid.NewGuid()}" })
        {
            var response = await client.GetAsync($"/api/v1/admin/audit-log{path}");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task ReturnsNewestFirstAcrossEveryServiceWithNamesResolved()
    {
        await SeedAsync();

        var page = await Get(AdminClient(), string.Empty);

        Assert.Equal(
            new[] { "plan.updated", AdminAuditLanguageActions.Disabled, "publish_rate_version", "reactivate", "suspend" },
            page!.Items.Select(item => item.Action).ToArray());
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
        Assert.All(page.Items, entry => Assert.Equal(DateTimeKind.Utc, entry.PerformedAt.Kind));

        var reactivate = page.Items.Single(item => item.Action == "reactivate");
        Assert.Equal("Ops Admin", reactivate.Actor.Name);
        Assert.Equal("ops@warptalk.io.vn", reactivate.Actor.Email);
        Assert.Equal("Acme Translation", reactivate.Entity.Label);
        Assert.Equal("acme-translation", reactivate.Entity.WorkspaceSlug);

        var language = page.Items.Single(item => item.Action == AdminAuditLanguageActions.Disabled);
        Assert.Equal("vi", language.Entity.Key);
        Assert.Equal("Vietnamese", language.Entity.Label);
    }

    [Fact]
    public async Task AFailedCommitSupersedesItsOwnAttempt()
    {
        await SeedAsync();

        var page = await Get(AdminClient(), $"?entityId={_planId}");

        var only = Assert.Single(page!.Items);
        Assert.Equal(AdminAuditResults.Failed, only.Result);
        Assert.Equal("trace-plan:failed", only.Request.CorrelationId);
    }

    [Fact]
    public async Task FiltersInTheDatabase()
    {
        await SeedAsync();
        var client = AdminClient();

        var byActor = await Get(client, $"?actorId={_otherAdminId}");
        Assert.Equal("reactivate", Assert.Single(byActor!.Items).Action);

        var byAction = await Get(client, "?action=suspend");
        Assert.Equal("Spam campaign reported", Assert.Single(byAction!.Items).Reason);

        var byEntity = await Get(client, $"?entityType={AdminAuditEntityTypes.UsageRate}&entityId={_rateId}");
        Assert.Equal(AdminAuditSources.BillingService, Assert.Single(byEntity!.Items).SourceService);

        var byKey = await Get(client, "?entityId=vi");
        Assert.Equal(AdminAuditEntityTypes.SupportedLanguage, Assert.Single(byKey!.Items).Entity.Type);

        var byWorkspace = await Get(client, $"?workspaceId={_workspaceId}");
        Assert.Equal(2, byWorkspace!.Items.Count);

        var failed = await Get(client, "?result=failed");
        Assert.Equal(2, failed!.Items.Count);
        Assert.Contains(failed.Items, item => item.ErrorMessage == "Rate overlaps an active card");

        var bySource = await Get(client, $"?sourceService={AdminAuditSources.TranslationRoomService}");
        Assert.Single(bySource!.Items);

        var byRange = await Get(client,
            $"?from={Uri.EscapeDataString(_now.AddMinutes(-11).ToString("O"))}&to={Uri.EscapeDataString(_now.AddMinutes(-4.5).ToString("O"))}");
        Assert.Equal(new[] { "publish_rate_version", "reactivate" }, byRange!.Items.Select(item => item.Action).ToArray());

        var rejected = await client.GetAsync("/api/v1/admin/audit-log?result=partly");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task FreeTextSearchesReasonsErrorsAddressesAndWorkspaceNames()
    {
        await SeedAsync();
        var client = AdminClient();

        Assert.Equal("suspend", Assert.Single((await Get(client, "?q=spam"))!.Items).Action);
        Assert.Equal("publish_rate_version", Assert.Single((await Get(client, "?q=overlaps"))!.Items).Action);
        Assert.Equal("publish_rate_version", Assert.Single((await Get(client, "?q=203.0.113"))!.Items).Action);
        Assert.Equal(AdminAuditLanguageActions.Disabled, Assert.Single((await Get(client, "?q=vietnam"))!.Items).Action);

        // Rows filed under a workspace are found by that workspace's name, though they never stored it.
        var byName = await Get(client, "?q=acme");
        Assert.Equal(2, byName!.Items.Count);

        // A LIKE wildcard typed into the box is text, not a pattern.
        Assert.Empty((await Get(client, "?q=%25"))!.Items);
    }

    [Fact]
    public async Task PagesWithACursorWithoutRepeatsOrGaps()
    {
        await SeedAsync();
        var client = AdminClient();

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await Get(client, "?limit=2" + (cursor is null ? string.Empty : $"&cursor={cursor}"));
            seen.AddRange(page!.Items.Select(item => item.Id));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(5, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());

        var bad = await client.GetAsync("/api/v1/admin/audit-log?cursor=not-a-cursor");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task PagesThroughRowsThatShareOneInstant()
    {
        await SeedAsync();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            var instant = _now.AddMinutes(-20);
            for (var i = 0; i < 5; i++)
            {
                db.WorkspaceAdminActions.Add(Row("note.added", instant, _adminUserId, AdminAuditEntityTypes.Workspace,
                    _workspaceId, _workspaceId));
            }

            await db.SaveChangesAsync();
        }

        var client = AdminClient();
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await Get(client, "?action=note.added&limit=2" + (cursor is null ? string.Empty : $"&cursor={cursor}"));
            seen.AddRange(page!.Items.Select(item => item.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null && seen.Count < 20);

        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public async Task ReadsOneEntryWithItsRequestMetadata()
    {
        await SeedAsync();
        var client = AdminClient();
        var id = (await Get(client, "?action=publish_rate_version"))!.Items.Single().Id;

        var entry = await client.GetFromJsonAsync<AdminAuditLogEntryDto>($"/api/v1/admin/audit-log/{id}");

        Assert.Equal("203.0.113.7", entry!.Request.IpAddress);
        Assert.Equal("Mozilla/5.0", entry.Request.UserAgent);
        Assert.Equal("trace-rate", entry.Request.CorrelationId);
        Assert.Equal("[redacted]", entry.AfterSummary!["apiKey"]);

        var missing = await client.GetAsync($"/api/v1/admin/audit-log/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task FacetsListWhatTheStoreHolds()
    {
        await SeedAsync();

        var facets = await AdminClient().GetFromJsonAsync<AdminAuditLogFacetsDto>("/api/v1/admin/audit-log/facets");

        Assert.Contains(facets!.Actions, item => item.Value == "suspend" && item.Count == 1);
        Assert.Contains(facets.EntityTypes, item => item.Value == AdminAuditEntityTypes.SupportedLanguage);
        Assert.Contains(facets.SourceServices, item => item.Value == AdminAuditSources.BillingService);
        var other = Assert.Single(facets.Actors, actor => actor.Id == _otherAdminId);
        Assert.Equal("Ops Admin", other.Name);
        Assert.Equal("root@warptalk.io.vn", Assert.Single(facets.Actors, actor => actor.Id == _adminUserId).Email);
    }

    [Fact]
    public async Task ExportsFilteredCsvAndRecordsTheExport()
    {
        await SeedAsync();
        var client = AdminClient();

        var response = await client.GetAsync("/api/v1/admin/audit-log/export?result=failed");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        var csv = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync()).TrimStart('﻿');
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("id,performed_at_utc,result,action", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains(lines, line => line.Contains("Rate overlaps an active card"));

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        var recorded = await db.WorkspaceAdminActions.SingleAsync(row => row.Action == AdminAuditLogActions.Exported);
        Assert.Equal(_adminUserId, recorded.PerformedBy);
        Assert.Equal(AdminAuditEntityTypes.AuditLog, recorded.EntityType);
        Assert.Equal("root@warptalk.io.vn", recorded.ActorEmail);
        // jsonb re-formats what it stores, so read the summary back as JSON rather than as text.
        var summary = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(recorded.AfterSummary!)!;
        Assert.Equal("failed", summary["result"]);
        Assert.Equal("2", summary["rows"]);
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
