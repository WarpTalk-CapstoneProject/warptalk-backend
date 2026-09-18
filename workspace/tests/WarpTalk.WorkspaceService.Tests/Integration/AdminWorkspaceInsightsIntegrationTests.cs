using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests.Integration;

/// <summary>
/// <c>GET /api/v1/admin/workspaces/insights</c> against real PostgreSQL — which also proves every
/// column the counts touch is mapped (WorkspaceDbContext maps each one by hand).
/// </summary>
public class AdminWorkspaceInsightsIntegrationTests : BaseIntegrationTest
{
    private const string Url = "/api/v1/admin/workspaces/insights";
    private const string Window = "?from=2026-09-08T00:00:00Z&to=2026-09-15T00:00:00Z";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static DateTime Utc(int month, int day, int hour = 0) =>
        new(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

    private HttpClient ClientWithRole(params string[] roles)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", GenerateJwtToken(Guid.NewGuid(), "someone@warptalk.io.vn", roles));
        return client;
    }

    private async Task SeedAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();

        db.Workspaces.AddRange(
            NewWorkspace("this-week", Utc(9, 9), isActive: true),
            // Suspended and created this week: counts in both.
            NewWorkspace("suspended-this-week", Utc(9, 14, 23), isActive: false),
            // Deleted since: still a creation of this week, but not "suspended now".
            NewWorkspace("deleted-this-week", Utc(9, 10), isActive: false, deletedAt: Utc(9, 16)),
            // On `to`: exclusive.
            NewWorkspace("on-boundary", Utc(9, 15), isActive: true),
            NewWorkspace("last-week", Utc(9, 3), isActive: true),
            NewWorkspace("suspended-long-ago", Utc(5, 1), isActive: false));

        await db.SaveChangesAsync();
    }

    private static Workspace NewWorkspace(string slug, DateTime createdAt, bool isActive, DateTime? deletedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = slug,
        Slug = slug,
        OwnerId = Guid.NewGuid(),
        Settings = "{}",
        IsActive = isActive,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        DeletedAt = deletedAt,
    };

    [Fact]
    public async Task Insights_RejectsUnauthenticatedCallers()
    {
        var response = await Factory.CreateClient().GetAsync(Url + Window);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData]
    [InlineData("Admin")]
    public async Task Insights_RejectsNonAdmins(params string[] roles)
    {
        var response = await ClientWithRole(roles).GetAsync(Url + Window);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("?from=2026-09-15T00:00:00Z&to=2026-09-08T00:00:00Z")]
    [InlineData("?from=2025-01-01T00:00:00Z&to=2026-09-08T00:00:00Z")]
    [InlineData(Window + "&compare=yesterday")]
    [InlineData(Window + "&tz=Not/AZone")]
    [InlineData(Window + "&tz=Asia%2F..%2F..%2Fpasswd")]
    public async Task Insights_RejectsAnInvalidWindow(string queryString)
    {
        var response = await ClientWithRole("admin").GetAsync(Url + queryString);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Insights_CountsNewWorkspacesAndSuspendedNow()
    {
        await SeedAsync();

        var response = await ClientWithRole("admin").GetAsync(Url + Window);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AdminWorkspaceInsightsDto>(Json);

        Assert.Equal(Utc(9, 1), body!.PreviousRange.From);
        Assert.Equal(Utc(9, 8), body.PreviousRange.To);

        var metric = Assert.Single(body.Metrics);
        Assert.Equal("newWorkspaces", metric.Id);
        Assert.Equal(3, metric.Value);
        Assert.Equal(1, metric.Previous);
        Assert.Equal("count", metric.Unit);
        Assert.Null(metric.Note);

        Assert.Equal(2, body.SuspendedNow);
    }

    [Fact]
    public async Task Insights_ComparesWithTheSameDaysLastMonth()
    {
        await SeedAsync();

        var body = await ClientWithRole("admin").GetFromJsonAsync<AdminWorkspaceInsightsDto>(
            Url + "?from=2026-06-01T00:00:00Z&to=2026-06-10T00:00:00Z&compare=previousMonth", Json);

        Assert.Equal(Utc(5, 1), body!.PreviousRange.From);
        Assert.Equal(Utc(5, 10), body.PreviousRange.To);
        Assert.Equal(0, body.Metrics.Single().Value);
        Assert.Equal(1, body.Metrics.Single().Previous);
    }

    [Fact]
    public async Task Insights_ShiftsPreviousMonthOnTheVietnamCalendar()
    {
        // Vietnam's 31 March 2026 as instants. Its previous-month counterpart is Vietnam's 28
        // February: [27 Feb 17:00Z, 28 Feb 17:00Z). Shifted on the UTC calendar instead, the same
        // instants would compare with a seven-hour sliver, [28 Feb 17:00Z, 1 Mar 00:00Z).
        var body = await ClientWithRole("admin").GetFromJsonAsync<AdminWorkspaceInsightsDto>(
            Url + "?from=2026-03-30T17:00:00Z&to=2026-03-31T17:00:00Z&compare=previousMonth", Json);

        Assert.Equal(new DateTime(2026, 2, 27, 17, 0, 0, DateTimeKind.Utc), body!.PreviousRange.From);
        Assert.Equal(new DateTime(2026, 2, 28, 17, 0, 0, DateTimeKind.Utc), body.PreviousRange.To);

        var utc = await ClientWithRole("admin").GetFromJsonAsync<AdminWorkspaceInsightsDto>(
            Url + "?from=2026-03-30T17:00:00Z&to=2026-03-31T17:00:00Z&compare=previousMonth&tz=UTC", Json);

        Assert.Equal(new DateTime(2026, 2, 28, 17, 0, 0, DateTimeKind.Utc), utc!.PreviousRange.From);
        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), utc.PreviousRange.To);
    }

    [Fact]
    public async Task Insights_SpeaksTheContractShape()
    {
        var response = await ClientWithRole("admin").GetAsync(Url + Window);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.True(root.GetProperty("range").TryGetProperty("from", out _));
        Assert.True(root.GetProperty("previousRange").TryGetProperty("to", out _));
        Assert.Equal(JsonValueKind.Number, root.GetProperty("suspendedNow").ValueKind);
        Assert.Equal("newWorkspaces", root.GetProperty("metrics")[0].GetProperty("id").GetString());
        Assert.True(root.GetProperty("metrics")[0].GetProperty("higherIsBetter").GetBoolean());
    }
}
