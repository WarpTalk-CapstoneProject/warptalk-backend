using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests.Integration;

/// <summary>
/// Addressing an admin workspace by slug (WT-560), against real PostgreSQL.
///
/// The portal's URL used to carry the workspace's primary key; it now carries the workspace's
/// own slug, which means a lookup that was a Guid equality is now a string one. That is the
/// part a mocked repository cannot show anything about, and it is where this can go wrong:
/// the same file already holds a folding, substring-matching search over exactly this column
/// for the directory, and the two must not be confused. A lookup is an identity, not a search.
/// </summary>
public class AdminWorkspaceBySlugIntegrationTests : BaseIntegrationTest
{
    private const string AdminRole = "admin";

    private readonly Guid _adminUserId = Guid.NewGuid();
    private readonly Guid _outsiderUserId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();

    private HttpClient AdminClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", GenerateJwtToken(_adminUserId, "root@warptalk.io.vn", AdminRole));
        return client;
    }

    private async Task<(Guid DemoId, Guid DemoTwoId, Guid DeletedId)> SeedAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();

        var demoId = Guid.NewGuid();
        var demoTwoId = Guid.NewGuid();
        var deletedId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.Workspaces.AddRange(
            // "demo" is a prefix of "demo-2". A substring match would find both, and which one
            // came back would depend on ordering — the failure this pair exists to catch.
            new Workspace
            {
                Id = demoId,
                Name = "Demo",
                Slug = "demo",
                OwnerId = _ownerId,
                Settings = "{}",
                IsActive = true,
                CreatedAt = now.AddDays(-10),
                UpdatedAt = now.AddDays(-2),
            },
            new Workspace
            {
                Id = demoTwoId,
                Name = "Demo Two",
                Slug = "demo-2",
                OwnerId = _ownerId,
                Settings = "{}",
                IsActive = true,
                CreatedAt = now.AddDays(-9),
                UpdatedAt = now.AddDays(-1),
            },
            new Workspace
            {
                Id = deletedId,
                Name = "Zenith Media",
                Slug = "zenith-media",
                OwnerId = _ownerId,
                Settings = "{}",
                IsActive = false,
                DeletedAt = now.AddDays(-1),
                CreatedAt = now.AddDays(-20),
                UpdatedAt = now.AddDays(-1),
            });

        await db.SaveChangesAsync();

        MockAuthIdentity.GetUserByIdAsync(_ownerId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = _ownerId, FullName = "Mai Tran", Email = "mai@acme.com" });

        return (demoId, demoTwoId, deletedId);
    }

    // ── the lookup is an identity, not a search ──────────────────────────────

    [Fact]
    public async Task BySlug_ResolvesExactlyOneWorkspace()
    {
        var (demoId, demoTwoId, _) = await SeedAsync();

        var demo = await AdminClient()
            .GetFromJsonAsync<AdminWorkspaceDetailDto>("/api/v1/admin/workspaces/by-slug/demo");
        var demoTwo = await AdminClient()
            .GetFromJsonAsync<AdminWorkspaceDetailDto>("/api/v1/admin/workspaces/by-slug/demo-2");

        Assert.Equal(demoId, demo!.Id);
        Assert.Equal("demo", demo.Slug);

        // The prefix must not have swallowed its longer namesake, nor the reverse.
        Assert.Equal(demoTwoId, demoTwo!.Id);
        Assert.Equal("demo-2", demoTwo.Slug);
    }

    [Fact]
    public async Task BySlug_IgnoresTheCaseTheUrlArrivedIn()
    {
        // Slugs are generated lowercase, but a URL survives being retyped, auto-capitalised by
        // a phone keyboard, or title-cased by a mail client before anyone clicks it.
        var (demoId, _, _) = await SeedAsync();

        var detail = await AdminClient()
            .GetFromJsonAsync<AdminWorkspaceDetailDto>("/api/v1/admin/workspaces/by-slug/DEMO");

        Assert.Equal(demoId, detail!.Id);
    }

    [Fact]
    public async Task BySlug_ReachesASoftDeletedWorkspace()
    {
        // The admin portal is the one surface that must reach a deleted workspace — reviewing
        // the deletion is the whole point. Its slug is still unique, because the row is still
        // there; that is what makes a slug safe to address these by at all.
        var (_, _, deletedId) = await SeedAsync();

        var detail = await AdminClient()
            .GetFromJsonAsync<AdminWorkspaceDetailDto>("/api/v1/admin/workspaces/by-slug/zenith-media");

        Assert.Equal(deletedId, detail!.Id);
        Assert.Equal(WorkspaceLifecycleStatus.Deleted, detail.Status);
    }

    [Fact]
    public async Task BySlug_CarriesTheSameDetailTheIdRouteDoes()
    {
        // The two routes must not drift: the portal reaches the very same page either way, and
        // an id link that redirects to the slug would otherwise land somewhere subtly different.
        var (demoId, _, _) = await SeedAsync();
        var client = AdminClient();

        var bySlug = await client
            .GetFromJsonAsync<AdminWorkspaceDetailDto>("/api/v1/admin/workspaces/by-slug/demo");
        var byId = await client
            .GetFromJsonAsync<AdminWorkspaceDetailDto>($"/api/v1/admin/workspaces/{demoId}");

        Assert.Equal(byId!.Id, bySlug!.Id);
        Assert.Equal(byId.Slug, bySlug.Slug);
        Assert.Equal(byId.Name, bySlug.Name);
        Assert.Equal(byId.Status, bySlug.Status);
        Assert.Equal(byId.MemberCount, bySlug.MemberCount);
        Assert.Equal(byId.Owner.FullName, bySlug.Owner.FullName);
    }

    [Fact]
    public async Task BySlug_UnknownSlugIsNotFound()
    {
        await SeedAsync();

        var response = await AdminClient().GetAsync("/api/v1/admin/workspaces/by-slug/nothing-here");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── the same gate as every other admin route ─────────────────────────────

    [Fact]
    public async Task BySlug_RejectsUnauthenticatedCallers()
    {
        var response = await Factory.CreateClient().GetAsync("/api/v1/admin/workspaces/by-slug/demo");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BySlug_RejectsAuthenticatedNonAdmins()
    {
        // A new route on an admin controller is a new way in if it is not covered by the same
        // policy. A slug is guessable in a way a Guid is not, which makes this the assertion
        // that matters most on this endpoint.
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", GenerateJwtToken(_outsiderUserId, "member@acme.com"));

        var response = await client.GetAsync("/api/v1/admin/workspaces/by-slug/demo");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task BySlug_RejectsTheWorkspaceScopedAdminRole()
    {
        // init-db.sql seeds both 'admin' (platform) and 'Admin' (workspace administrator).
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", GenerateJwtToken(_outsiderUserId, "workspace-admin@acme.com", "Admin"));

        var response = await client.GetAsync("/api/v1/admin/workspaces/by-slug/demo");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
