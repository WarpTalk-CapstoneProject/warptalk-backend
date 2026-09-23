using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.TranslationRoomService.Application.DTOs.Admin;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Integration;

/// <summary>
/// WT-693: <c>/admin/meetings</c> showed "Meetings could not be loaded" on every visit.
///
/// The page's default tab is "all", and it sent that value straight through as
/// <c>?status=all</c>. The service validated the status against the real statuses plus "live" —
/// "all" was not among them — and answered 400 "Unknown status" to the one request every visit
/// makes first. These tests send the query strings exactly as the web client builds them, through
/// the real controller, policy and PostgreSQL.
/// </summary>
public sealed class AdminMeetingDirectoryIntegrationTests : BaseIntegrationTest
{
    private const string Url = "/api/v1/admin/meetings";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly DateTime Anchor = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    private static HttpRequestMessage Get(string url, string? roles = "admin")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (roles is not null) request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        return request;
    }

    private async Task SeedAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
        db.TranslationRooms.AddRange(
            Room("ENDED", startedAt: Anchor, endedAt: Anchor.AddMinutes(30)),
            Room("IN_PROGRESS", startedAt: Anchor.AddDays(1)),
            Room("SCHEDULED", scheduledAt: Anchor.AddDays(5)));
        await db.SaveChangesAsync();
    }

    private static TranslationRoom Room(
        string status,
        DateTime? scheduledAt = null,
        DateTime? startedAt = null,
        DateTime? endedAt = null)
    {
        var id = Guid.NewGuid();
        return new TranslationRoom
        {
            Id = id,
            WorkspaceId = Guid.NewGuid(),
            HostId = Guid.NewGuid(),
            Title = $"Room {status}",
            TranslationRoomCode = $"WARP-{id.ToString("N")[..6]}",
            Status = status,
            TranslationRoomType = "STANDARD",
            MaxParticipants = 10,
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\"]",
            Settings = "{}",
            ScheduledAt = scheduledAt,
            StartedAt = startedAt,
            EndedAt = endedAt,
            IsActive = true,
            CreatedAt = Anchor.AddDays(-1),
            UpdatedAt = Anchor.AddDays(-1),
        };
    }

    [Fact]
    public async Task The_default_all_tab_lists_every_meeting()
    {
        await SeedAsync();

        // Byte for byte what the page requested on first paint.
        var response = await Client.SendAsync(Get(Url + "?page=1&pageSize=20&status=all&sort=recent_desc"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AdminPagedResult<AdminMeetingSummaryDto>>(Json);
        Assert.Equal(3, body!.Total);
    }

    [Theory]
    [InlineData("live", 1)]
    [InlineData("SCHEDULED", 1)]
    [InlineData("ENDED", 1)]
    [InlineData("CANCELLED", 0)]
    [InlineData("FAILED", 0)]
    [InlineData("EXPIRED", 0)]
    public async Task Every_status_tab_the_page_offers_is_accepted(string status, int expected)
    {
        await SeedAsync();

        var response = await Client.SendAsync(Get($"{Url}?page=1&pageSize=20&status={status}&sort=recent_desc"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AdminPagedResult<AdminMeetingSummaryDto>>(Json);
        Assert.Equal(expected, body!.Total);
    }

    [Theory]
    [InlineData("recent_asc")]
    [InlineData("duration_desc")]
    public async Task Every_sort_the_page_offers_is_accepted(string sort)
    {
        await SeedAsync();

        var response = await Client.SendAsync(Get($"{Url}?page=1&pageSize=20&status=all&sort={sort}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_status_is_still_a_400()
    {
        var response = await Client.SendAsync(Get(Url + "?status=bogus"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_header_counts_load()
    {
        await SeedAsync();

        var response = await Client.SendAsync(Get(Url + "/counts"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AdminMeetingCountsDto>(Json);
        Assert.Equal(1, body!.LiveNow);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Member")]
    public async Task Non_admins_are_refused(string? roles)
    {
        var response = await Client.SendAsync(Get(Url + "?status=all", roles));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
