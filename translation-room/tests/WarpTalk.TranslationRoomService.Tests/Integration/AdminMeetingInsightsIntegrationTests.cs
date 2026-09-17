using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.TranslationRoomService.Application.DTOs.Admin;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Integration;

/// <summary>
/// <c>GET /api/v1/admin/meetings/insights</c> against real PostgreSQL: the policy, the 400s, the
/// span query's translation, and the two definitions on seeded rooms. The clipping maths itself
/// is pinned in AdminMeetingInsightsCalculatorTests.
/// </summary>
public sealed class AdminMeetingInsightsIntegrationTests : BaseIntegrationTest
{
    private const string Url = "/api/v1/admin/meetings/insights";
    // On the UTC calendar, so the day keys asserted below are UTC days; the default Vietnam calendar
    // has its own test.
    private const string Window = "?from=2026-09-08T00:00:00Z&to=2026-09-15T00:00:00Z&tz=UTC";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static DateTime Utc(int month, int day, int hour = 0, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    private HttpRequestMessage Get(string url, string? roles)
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
            // 2 h on 9 Sep.
            Room("ENDED", startedAt: Utc(9, 9, 10), endedAt: Utc(9, 9, 12)),
            // Across midnight into 10 Sep: 1 h + 1 h.
            Room("ENDED", startedAt: Utc(9, 9, 23), endedAt: Utc(9, 10, 1)),
            // Started last week, ran 1 h into this one: hours here, held there.
            Room("ENDED", startedAt: Utc(9, 7, 23), endedAt: Utc(9, 8, 1)),
            // Held this week, end never stamped: held, excluded from hours, noted.
            Room("CANCELLED", startedAt: Utc(9, 11, 9)),
            // Scheduled only, never started: not held.
            Room("SCHEDULED", scheduledAt: Utc(9, 12, 9)),
            // Deleted: invisible.
            Room("ENDED", startedAt: Utc(9, 12, 9), endedAt: Utc(9, 12, 10), deletedAt: Utc(9, 13)),
            // Last week: 0.5 h.
            Room("ENDED", startedAt: Utc(9, 2, 9), endedAt: Utc(9, 2, 9, 30)));

        await db.SaveChangesAsync();
    }

    private static TranslationRoom Room(
        string status,
        DateTime? scheduledAt = null,
        DateTime? startedAt = null,
        DateTime? endedAt = null,
        DateTime? deletedAt = null)
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
            CreatedAt = Utc(8, 1),
            UpdatedAt = Utc(8, 1),
            DeletedAt = deletedAt,
        };
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Admin")]
    [InlineData("Member")]
    public async Task Insights_RejectsNonAdmins(string? roles)
    {
        var response = await Client.SendAsync(Get(Url + Window, roles));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("?from=2026-09-15T00:00:00Z&to=2026-09-08T00:00:00Z")]
    [InlineData("?from=2025-01-01T00:00:00Z&to=2026-09-08T00:00:00Z")]
    [InlineData(Window + "&compare=lastWeek")]
    [InlineData("?from=2026-09-08T00:00:00Z&to=2026-09-15T00:00:00Z&tz=Europe/Atlantis")]
    public async Task Insights_RejectsAnInvalidWindow(string queryString)
    {
        var response = await Client.SendAsync(Get(Url + queryString, "admin"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Insights_ReportsMeetingsHeldAndClippedHours()
    {
        await SeedAsync();

        var response = await Client.SendAsync(Get(Url + Window, "admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AdminMeetingInsightsDto>(Json);

        Assert.Equal(Utc(9, 1), body!.PreviousRange.From);
        Assert.Equal(Utc(9, 8), body.PreviousRange.To);

        var held = body.Metrics.Single(m => m.Id == "meetingsHeld");
        Assert.Equal(3, held.Value);
        Assert.Equal(2, held.Previous);
        Assert.Equal("count", held.Unit);

        var hours = body.Metrics.Single(m => m.Id == "hoursTranslated");
        Assert.Equal(5m, hours.Value);
        Assert.Equal(1.5m, hours.Previous);
        Assert.Equal("hours", hours.Unit);
        Assert.Contains("no recorded end time (1 this period, 0 previous)", hours.Note);

        Assert.Equal(7, body.MeetingsByDay.Count);
        Assert.Equal((0, 1m), Day(body, "2026-09-08"));
        Assert.Equal((2, 3m), Day(body, "2026-09-09"));
        Assert.Equal((0, 1m), Day(body, "2026-09-10"));
        Assert.Equal((1, 0m), Day(body, "2026-09-11"));
        Assert.Equal((0, 0m), Day(body, "2026-09-12"));

        Assert.Equal(0, body.LiveNow);
        Assert.Equal(0, body.StartedToday);
    }

    [Fact]
    public async Task Insights_SplitsDaysAtVietnamMidnightByDefault()
    {
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
            // 23:00 on 9 Sep → 01:00 on 10 Sep in Vietnam; in UTC both ends are on the 9th.
            db.TranslationRooms.Add(Room("ENDED", startedAt: Utc(9, 9, 16), endedAt: Utc(9, 9, 18)));
            await db.SaveChangesAsync();
        }

        // Vietnam's 9 and 10 September, as instants; no tz, so Asia/Ho_Chi_Minh.
        var response = await Client.SendAsync(Get(Url + "?from=2026-09-08T17:00:00Z&to=2026-09-10T17:00:00Z", "admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AdminMeetingInsightsDto>(Json);

        Assert.Equal(new[] { "2026-09-09", "2026-09-10" }, body!.MeetingsByDay.Select(d => d.Date).ToArray());
        Assert.Equal((1, 1m), Day(body, "2026-09-09"));
        Assert.Equal((0, 1m), Day(body, "2026-09-10"));
    }

    private static (int, decimal) Day(AdminMeetingInsightsDto body, string date)
    {
        var day = body.MeetingsByDay.Single(d => d.Date == date);
        return (day.Meetings, day.Hours);
    }

    [Fact]
    public async Task Insights_SpeaksTheContractShape()
    {
        var response = await Client.SendAsync(Get(Url + Window, "admin"));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.True(root.GetProperty("range").TryGetProperty("from", out _));
        Assert.True(root.GetProperty("previousRange").TryGetProperty("to", out _));
        Assert.Equal(JsonValueKind.Number, root.GetProperty("liveNow").ValueKind);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("startedToday").ValueKind);
        var day = root.GetProperty("meetingsByDay")[0];
        Assert.Equal("2026-09-08", day.GetProperty("date").GetString());
        Assert.Equal(JsonValueKind.Number, day.GetProperty("meetings").ValueKind);
        Assert.Equal(JsonValueKind.Number, day.GetProperty("hours").ValueKind);
        var metric = root.GetProperty("metrics")[1];
        Assert.Equal("hoursTranslated", metric.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, metric.GetProperty("note").ValueKind);
    }
}
