using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Integration;

/// <summary>
/// WT-694 through the real controller and PostgreSQL: the summary carries the previous window,
/// per-dimension confidence, the weakest dimension and the dimensions with no data; comments can be
/// read lowest-rated first.
/// </summary>
public sealed class AdminFeedbackInsightsIntegrationTests : BaseIntegrationTest
{
    private const string Summary = "/api/v1/admin/feedback/summary";
    private const string Comments = "/api/v1/admin/feedback/comments";

    // Current window: 10–20 Sep. Previous window: 31 Aug–10 Sep.
    private const string Window = "?from=2026-09-10T00:00:00Z&to=2026-09-20T00:00:00Z";

    private static DateTime Utc(int month, int day) => new(2026, month, day, 9, 0, 0, DateTimeKind.Utc);

    private static HttpRequestMessage Get(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestAuthHandler.RolesHeader, "admin");
        return request;
    }

    private async Task SeedAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();

        var current = Room(Utc(9, 12));
        var earlier = Room(Utc(9, 2));
        db.TranslationRooms.AddRange(current, earlier);

        // 12 current ratings: overall 4 everywhere, translation 3 on all, audio on 2 only, never a clone rating.
        for (var i = 0; i < 12; i++)
        {
            db.TranslationRoomFeedbacks.Add(Feedback(current.Id, overall: 4, translation: 3,
                audio: i < 2 ? 2 : null, comment: i == 0 ? "Fine overall." : null, at: Utc(9, 13)));
        }

        db.TranslationRoomFeedbacks.Add(Feedback(current.Id, overall: 1, translation: 1,
            comment: "Translation fell apart.", at: Utc(9, 14)));

        // Previous window: overall 3.
        for (var i = 0; i < 10; i++)
        {
            db.TranslationRoomFeedbacks.Add(Feedback(earlier.Id, overall: 3, at: Utc(9, 3)));
        }

        await db.SaveChangesAsync();
    }

    private static TranslationRoom Room(DateTime endedAt)
    {
        var id = Guid.NewGuid();
        return new TranslationRoom
        {
            Id = id,
            WorkspaceId = Guid.NewGuid(),
            HostId = Guid.NewGuid(),
            Title = "Weekly sync",
            TranslationRoomCode = $"WARP-{id.ToString("N")[..6]}",
            Status = "ENDED",
            TranslationRoomType = "STANDARD",
            MaxParticipants = 10,
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\"]",
            Settings = "{}",
            StartedAt = endedAt.AddHours(-1),
            EndedAt = endedAt,
            IsActive = true,
            CreatedAt = endedAt.AddDays(-1),
            UpdatedAt = endedAt,
        };
    }

    private static TranslationRoomFeedback Feedback(
        Guid roomId, int overall, int? translation = null, int? audio = null, string? comment = null, DateTime at = default) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = roomId,
        UserId = Guid.NewGuid(),
        OverallRating = overall,
        TranslationQuality = translation,
        AudioQuality = audio,
        Comments = comment,
        CreatedAt = at,
    };

    [Fact]
    public async Task The_summary_carries_confidence_trend_lowest_and_no_data_dimensions()
    {
        await SeedAsync();

        var response = await Client.SendAsync(Get(Summary + Window));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(13, root.GetProperty("responseCount").GetInt32());
        Assert.Equal(10, root.GetProperty("previousResponseCount").GetInt32());
        Assert.Equal("2026-08-31T00:00:00Z", root.GetProperty("previousFrom").GetDateTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
        Assert.Equal("ok", root.GetProperty("confidence").GetString());
        Assert.Equal(10, root.GetProperty("minResponses").GetInt32());

        // Translation: 13 answers averaging (12*3+1)/13 ≈ 2.85 — lower than overall's (12*4+1)/13.
        // Audio averages 2.0 but from 2 answers, so it is thin and not the headline.
        Assert.Equal("translationQuality", root.GetProperty("lowestDimension").GetString());

        var withoutData = root.GetProperty("dimensionsWithoutData").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("voiceCloneQuality", withoutData);
        Assert.Contains("aiSummaryQuality", withoutData);

        var dimensions = root.GetProperty("dimensions").EnumerateArray().ToDictionary(
            d => d.GetProperty("dimension").GetString()!, d => d);

        var overall = dimensions["overallRating"];
        Assert.Equal("ok", overall.GetProperty("confidence").GetString());
        Assert.Equal(3.0, overall.GetProperty("previousAverageRating").GetDouble());
        Assert.Equal(0.77, overall.GetProperty("averageDelta").GetDouble(), 2);

        var audio = dimensions["audioQuality"];
        Assert.Equal("low", audio.GetProperty("confidence").GetString());
        Assert.Equal(JsonValueKind.Null, audio.GetProperty("averageDelta").ValueKind);

        var clone = dimensions["voiceCloneQuality"];
        Assert.Equal("none", clone.GetProperty("confidence").GetString());
        Assert.Equal(JsonValueKind.Null, clone.GetProperty("averageRating").ValueKind);
    }

    [Fact]
    public async Task Comments_can_be_read_lowest_rated_first()
    {
        await SeedAsync();

        var response = await Client.SendAsync(Get(Comments + Window + "&sort=lowest"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var ratings = document.RootElement.GetProperty("items").EnumerateArray()
            .Select(c => c.GetProperty("overallRating").GetInt32()).ToArray();
        Assert.Equal(new[] { 1, 4 }, ratings);
    }

    [Fact]
    public async Task An_unknown_comment_sort_is_a_400()
    {
        var response = await Client.SendAsync(Get(Comments + Window + "&sort=loudest"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
