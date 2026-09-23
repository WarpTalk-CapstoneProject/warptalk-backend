using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using WarpTalk.AuthService.Application.DTOs.Admin;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Infrastructure.Persistence;
using WarpTalk.AuthService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.AuthService.Tests.Integration;

/// <summary>
/// <c>GET /api/v1/admin/users/insights</c> end to end against real PostgreSQL: the policy, the
/// 400s, and both metric definitions on seeded rows.
/// </summary>
public sealed class AdminUserInsightsIntegrationTests : BaseIntegrationTest
{
    private const string Url = "/api/v1/admin/users/insights";

    // Current window [8 Sep, 15 Sep); previous window [1 Sep, 8 Sep). On the UTC calendar, so the
    // day keys below are UTC days; the Vietnam calendar (the default tz) has its own tests.
    private const string Window = "?from=2026-09-08T00:00:00Z&to=2026-09-15T00:00:00Z&tz=UTC";

    private static DateTime Utc(int month, int day, int hour = 0, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient ClientWithRole(params string[] roles)
    {
        var handler = new JwtSecurityTokenHandler();
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Email, "root@warptalk.io.vn"),
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = "WarpTalk.AuthService",
            Audience = "WarpTalk",
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("CHANGE_ME_SUPER_SECRET_KEY_MIN_32_CHARS_LONG!!")),
                SecurityAlgorithms.HmacSha256Signature),
        });

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", handler.WriteToken(token));
        return client;
    }

    private async Task SeedAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var inWindow = NewUser("a@acme.com", Utc(9, 9, 10));
        var lateOnLastDay = NewUser("b@acme.com", Utc(9, 14, 23, 30));
        // Deleted since: still a sign-up of that week.
        var deletedSince = NewUser("c@acme.com", Utc(9, 10, 8), deletedAt: Utc(9, 16));
        // Exactly on `to`: exclusive, belongs to nobody here.
        var onBoundary = NewUser("d@acme.com", Utc(9, 15));
        var previousWeek = NewUser("e@acme.com", Utc(9, 2, 12));
        var longAgo = NewUser("f@acme.com", Utc(6, 1));

        db.Users.AddRange(inWindow, lateOnLastDay, deletedSince, onBoundary, previousWeek, longAgo);

        db.RefreshTokens.AddRange(
            // longAgo signed in on 1 Sep and refreshed twice on 10 Sep: one active user this week,
            // however many rows. Their last_login_at says June — the undercount this avoids.
            NewToken(longAgo.Id, Utc(9, 10, 9)),
            NewToken(longAgo.Id, Utc(9, 10, 9, 30)),
            NewToken(inWindow.Id, Utc(9, 9, 10)),
            // Revoked since: it was still issued, so the user was still active.
            NewToken(lateOnLastDay.Id, Utc(9, 14, 23, 31), revokedAt: Utc(9, 15, 1)),
            // Previous window only.
            NewToken(previousWeek.Id, Utc(9, 2, 12)),
            NewToken(longAgo.Id, Utc(9, 1, 8)),
            // On `to`: excluded.
            NewToken(onBoundary.Id, Utc(9, 15)));

        await db.SaveChangesAsync();
    }

    private static User NewUser(string email, DateTime createdAt, DateTime? deletedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        FullName = email,
        PreferredLanguage = "en",
        Timezone = "UTC",
        IsActive = true,
        EmailVerified = true,
        // Deliberately stale: activeUsers must not read it.
        LastLoginAt = Utc(6, 1),
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        DeletedAt = deletedAt,
    };

    private static RefreshToken NewToken(Guid userId, DateTime createdAt, DateTime? revokedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        FamilyId = Guid.NewGuid(),
        TokenHash = Guid.NewGuid().ToString("N"),
        CreatedAt = createdAt,
        ExpiresAt = createdAt.AddDays(7),
        RevokedAt = revokedAt,
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
    [InlineData("Member")]
    public async Task Insights_RejectsNonAdmins(params string[] roles)
    {
        var response = await ClientWithRole(roles).GetAsync(Url + Window);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("?from=2026-09-15T00:00:00Z&to=2026-09-08T00:00:00Z")]
    [InlineData("?from=2025-01-01T00:00:00Z&to=2026-09-08T00:00:00Z")]
    [InlineData(Window + "&compare=lastYear")]
    [InlineData("?from=2026-09-08T00:00:00Z&to=2026-09-15T00:00:00Z&tz=Mars/Olympus_Mons")]
    [InlineData("?from=2026-09-08T00:00:00Z&to=2026-09-15T00:00:00Z&tz=../../etc/passwd")]
    [InlineData("?from=2026-09-08T00:00:00Z&to=2026-09-15T00:00:00Z&tz=%2B07:00")]
    public async Task Insights_RejectsAnInvalidWindow(string queryString)
    {
        var response = await ClientWithRole("admin").GetAsync(Url + queryString);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Insights_CountsNewAndActiveUsersPerDefinition()
    {
        await SeedAsync();

        var response = await ClientWithRole("admin").GetAsync(Url + Window);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AdminUserInsightsDto>(Json);

        Assert.NotNull(body);
        Assert.Equal(Utc(9, 8), body!.Range.From);
        Assert.Equal(Utc(9, 15), body.Range.To);
        Assert.Equal(Utc(9, 1), body.PreviousRange.From);
        Assert.Equal(Utc(9, 8), body.PreviousRange.To);

        var newUsers = body.Metrics.Single(m => m.Id == "newUsers");
        Assert.Equal(3, newUsers.Value);
        Assert.Equal(1, newUsers.Previous);
        Assert.Equal("count", newUsers.Unit);
        Assert.True(newUsers.HigherIsBetter);

        var activeUsers = body.Metrics.Single(m => m.Id == "activeUsers");
        Assert.Equal(3, activeUsers.Value);
        Assert.Equal(2, activeUsers.Previous);

        Assert.Equal(7, body.NewUsersByDay.Count);
        Assert.Equal("2026-09-08", body.NewUsersByDay[0].Date);
        Assert.Equal(0, body.NewUsersByDay[0].Count);
        Assert.Equal(1, body.NewUsersByDay.Single(d => d.Date == "2026-09-09").Count);
        Assert.Equal(1, body.NewUsersByDay.Single(d => d.Date == "2026-09-10").Count);
        Assert.Equal(1, body.NewUsersByDay.Single(d => d.Date == "2026-09-14").Count);
    }

    /// <summary>
    /// WT-692: the investor view on /admin/billing reads user growth month by month — the six
    /// local months ending with the month of <c>to</c>, like billing's revenueByMonth.
    /// </summary>
    [Fact]
    public async Task Insights_ReportsSixMonthsOfUserGrowth()
    {
        await SeedAsync();

        var body = await ClientWithRole("admin").GetFromJsonAsync<AdminUserInsightsDto>(Url + Window, Json);

        var months = body!.UsersByMonth!;
        Assert.Equal(new[] { "2026-04", "2026-05", "2026-06", "2026-07", "2026-08", "2026-09" }, months.Select(m => m.Month));

        var june = months.Single(m => m.Month == "2026-06");
        Assert.Equal((1, 1, 0), (june.NewUsers, june.TotalUsers, june.ActiveUsers));
        var august = months.Single(m => m.Month == "2026-08");
        Assert.Equal((0, 1, 0), (august.NewUsers, august.TotalUsers, august.ActiveUsers));

        // September: five sign-ups (the one deleted on 16 Sep still signed up), but only four of
        // them plus June's account still exist at the month's end; five distinct accounts signed in.
        var september = months.Single(m => m.Month == "2026-09");
        Assert.Equal(5, september.NewUsers);
        Assert.Equal(5, september.TotalUsers);
        Assert.Equal(5, september.ActiveUsers);
        Assert.Contains("month-to-date", body.UsersByMonthNote);
    }

    [Fact]
    public async Task Insights_ComparesWithTheSameDaysLastMonth()
    {
        await SeedAsync();

        var body = await ClientWithRole("admin").GetFromJsonAsync<AdminUserInsightsDto>(
            Url + "?from=2026-09-01T00:00:00Z&to=2026-09-15T00:00:00Z&compare=previousMonth&tz=UTC", Json);

        Assert.Equal(Utc(8, 1), body!.PreviousRange.From);
        Assert.Equal(Utc(8, 15), body.PreviousRange.To);
        Assert.Equal(4, body.Metrics.Single(m => m.Id == "newUsers").Value);
        Assert.Equal(0, body.Metrics.Single(m => m.Id == "newUsers").Previous);
    }

    [Fact]
    public async Task Insights_SpeaksTheContractShape()
    {
        var response = await ClientWithRole("admin").GetAsync(Url + Window);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Object, root.GetProperty("range").ValueKind);
        Assert.True(root.GetProperty("previousRange").TryGetProperty("from", out _));
        var metric = root.GetProperty("metrics")[0];
        foreach (var name in new[] { "id", "value", "previous", "unit", "higherIsBetter", "note" })
        {
            Assert.True(metric.TryGetProperty(name, out _), $"metric is missing '{name}'");
        }

        var day = root.GetProperty("newUsersByDay")[0];
        Assert.True(day.TryGetProperty("date", out _));
        Assert.True(day.TryGetProperty("count", out _));
    }

    [Fact]
    public async Task Insights_BucketsDaysOnTheVietnamCalendarByDefault()
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Users.AddRange(
                // 23:59 on 9 Sep in Vietnam.
                NewUser("late@acme.com", Utc(9, 9, 16, 59)),
                // 17:00Z is midnight in Vietnam: already 10 Sep there, still 9 Sep in UTC.
                NewUser("midnight@acme.com", Utc(9, 9, 17)),
                NewUser("morning@acme.com", Utc(9, 10, 1)));
            await db.SaveChangesAsync();
        }

        // Vietnam's 9 and 10 September, as instants. No tz: the default is Asia/Ho_Chi_Minh.
        var body = await ClientWithRole("admin").GetFromJsonAsync<AdminUserInsightsDto>(
            Url + "?from=2026-09-08T17:00:00Z&to=2026-09-10T17:00:00Z", Json);

        Assert.Equal(Utc(9, 8, 17), body!.Range.From);
        Assert.Equal(Utc(9, 10, 17), body.Range.To);
        Assert.Equal(3, body.Metrics.Single(m => m.Id == "newUsers").Value);
        Assert.Equal(
            new[] { ("2026-09-09", 1), ("2026-09-10", 2) },
            body.NewUsersByDay.Select(d => (d.Date, d.Count)).ToArray());
    }

    [Fact]
    public async Task Insights_ComparesAVietnamMonthWithTheVietnamMonthBefore()
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Users.AddRange(
                // 1 Aug 06:00 in Vietnam: August there, July in UTC.
                NewUser("aug-first@acme.com", Utc(7, 31, 23)),
                // 1 Sep 00:30 in Vietnam: September there, August in UTC.
                NewUser("sep-first@acme.com", Utc(8, 31, 17, 30)));
            await db.SaveChangesAsync();
        }

        var body = await ClientWithRole("admin").GetFromJsonAsync<AdminUserInsightsDto>(
            Url + "?from=2026-08-31T17:00:00Z&to=2026-09-30T17:00:00Z&compare=previousMonth&tz=Asia/Ho_Chi_Minh", Json);

        Assert.Equal(Utc(7, 31, 17), body!.PreviousRange.From);
        Assert.Equal(Utc(8, 31, 17), body.PreviousRange.To);
        var newUsers = body.Metrics.Single(m => m.Id == "newUsers");
        Assert.Equal(1, newUsers.Value);
        Assert.Equal(1, newUsers.Previous);
        Assert.Equal(30, body.NewUsersByDay.Count);
        Assert.Equal("2026-09-01", body.NewUsersByDay[0].Date);
        Assert.Equal(1, body.NewUsersByDay[0].Count);
        Assert.Equal("2026-09-30", body.NewUsersByDay[^1].Date);
    }

    [Fact]
    public async Task CreatedAt_ComesBackAsUtcInstantsWhateverTheSessionTimeZone()
    {
        await SeedAsync();

        using var scope = Factory.Services.CreateScope();
        var connectionString = scope.ServiceProvider.GetRequiredService<AuthDbContext>()
            .Database.GetConnectionString();

        // A session in UTC+7 must not shift the instants the service buckets.
        await using var context = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(connectionString + ";Timezone=Asia/Ho_Chi_Minh")
            .Options);

        var instants = await new UserRepository(context).GetCreatedAtBetweenAsync(Utc(9, 8), Utc(9, 15));

        Assert.Contains(Utc(9, 14, 23, 30), instants);
        Assert.DoesNotContain(Utc(9, 15), instants);
        Assert.All(instants, at => Assert.Equal(DateTimeKind.Utc, at.Kind));
    }
}
