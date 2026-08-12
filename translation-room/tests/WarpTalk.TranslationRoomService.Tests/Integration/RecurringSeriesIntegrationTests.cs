using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace WarpTalk.TranslationRoomService.Tests.Integration;

/// <summary>
/// WT-327, end to end against a real Postgres.
///
/// This is the test that actually proves the feature, because the interesting behaviour is not
/// in any one method — it is that a rule turns into N ordinary rooms, at the right instants, in
/// the status the reminder sweep can see, and that the horizon keeps moving without anybody
/// asking it to.
///
/// The clock is injectable so "two days pass" is a variable assignment rather than a two-day
/// test. Everything else — HTTP, routing, validation, EF, the unique index — is real.
/// </summary>
public class RecurringSeriesIntegrationTests : IAsyncLifetime
{
    private const string Hcm = "Asia/Ho_Chi_Minh";

    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    /// <summary>The clock the series service reads. Moving it is how this test makes days pass.</summary>
    private DateTime _now = new(2026, 8, 6, 3, 0, 0, DateTimeKind.Utc); // 10:00 in Ho Chi Minh City

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Grpc:InternalSecret", "test-only-internal-grpc-secret-32-characters");
            builder.ConfigureTestServices(services =>
            {
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<TranslationRoomDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<TranslationRoomDbContext>(o => o.UseNpgsql(_dbContainer.GetConnectionString()));

                var redisDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IConnectionMultiplexer));
                if (redisDescriptor != null) services.Remove(redisDescriptor);
                services.AddSingleton(BuildRedisStub());

                var policyDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IWorkspaceMeetingPolicy));
                if (policyDescriptor != null) services.Remove(policyDescriptor);
                var meetingPolicy = new Mock<IWorkspaceMeetingPolicy>();
                meetingPolicy.Setup(p => p.ValidateMeetingCreationAsync(
                        It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Result.Success());

                // ...and the tenant itself is live unless a test suspends it.
                meetingPolicy.Setup(p => p.EnsureWorkspaceCanHostMeetingsAsync(
                        It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Result.Success());
                services.AddScoped(_ => meetingPolicy.Object);

                // The one substitution that matters: a clock this test owns.
                var seriesDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ITranslationRoomSeriesService));
                if (seriesDescriptor != null) services.Remove(seriesDescriptor);
                services.AddScoped<ITranslationRoomSeriesService>(sp => new TranslationRoomSeriesService(
                    sp.GetRequiredService<IUnitOfWork>(),
                    sp.GetRequiredService<ITranslationRoomService>(),
                    sp.GetRequiredService<ILogger<TranslationRoomSeriesService>>(),
                    () => _now));

                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.AddAuthorization(options =>
                {
                    options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder("Test")
                        .RequireAuthenticatedUser().Build();
                });
            });
        });

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Test");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
        db.Database.ExecuteSqlRaw("CREATE EXTENSION IF NOT EXISTS pgcrypto;");
        db.Database.ExecuteSqlRaw("CREATE OR REPLACE FUNCTION public.uuidv7() RETURNS uuid AS $$ BEGIN RETURN gen_random_uuid(); END; $$ LANGUAGE plpgsql;");
        await db.Database.EnsureCreatedAsync();
        db.Database.ExecuteSqlRaw("CREATE SCHEMA IF NOT EXISTS translation_room;");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS translation_room.supported_languages (code VARCHAR(15) PRIMARY KEY, name VARCHAR(100) NOT NULL, native_name VARCHAR(100), is_active BOOLEAN NOT NULL DEFAULT TRUE);");
        db.Database.ExecuteSqlRaw("INSERT INTO translation_room.supported_languages (code, name, native_name) VALUES ('en-US','English','English'),('vi-VN','Vietnamese','Tiếng Việt') ON CONFLICT DO NOTHING;");
    }

    public async Task DisposeAsync()
    {
        await _dbContainer.DisposeAsync();
        _factory.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Daily_at_eight_am_materialises_the_horizon_at_one_am_UTC_every_day()
    {
        var hostId = Guid.NewGuid();
        var response = await CreateDailyAsync(hostId, "08:00", endDateLocal: "2026-09-05");

        response.Series.Type.Should().Be(RecurrenceTypes.Daily);
        response.Series.StartTimeLocal.Should().Be("08:00");
        response.Series.TimeZone.Should().Be(Hcm);

        // Booked at 10:00 local, so the first 08:00 is tomorrow, 2026-08-07.
        response.Series.StartDateLocal.Should().Be("2026-08-07");

        // Horizon is 14 days from today (the 6th) => through the 20th => 14 occurrences.
        response.MaterializedOccurrenceCount.Should().Be(14);
        response.TotalOccurrenceCount.Should().Be(30); // 08-07 .. 09-05 inclusive

        var rooms = await OccurrencesOfAsync(response.Series.SeriesId);
        rooms.Should().HaveCount(14);

        // Every occurrence is one day apart, at 01:00 UTC == 08:00 Ho Chi Minh City.
        rooms.Select(r => r.ScheduledAt!.Value)
            .Should().BeEquivalentTo(
                Enumerable.Range(0, 14).Select(i => new DateTime(2026, 8, 7, 1, 0, 0, DateTimeKind.Utc).AddDays(i)),
                options => options.WithStrictOrdering());

        // SCHEDULED with a non-null scheduled_at is exactly what ReminderNotificationWorker
        // filters on, so occurrences get their T-10min/T-1min reminders like any booked meeting.
        rooms.Should().OnlyContain(r => r.Status == "SCHEDULED");
        rooms.Should().OnlyContain(r => r.ScheduledAt != null);

        // One room ROW per meeting — the invariant the whole design exists to preserve, so each
        // day keeps its own transcript, artifacts and billing.
        rooms.Select(r => r.Id).Distinct().Should().HaveCount(14);

        // ...but ONE CODE for the booking. Thirty codes for one standup meant the invite sent on
        // Monday opened Monday's room forever; by Wednesday it pointed at a meeting that had
        // already ended.
        rooms.Select(r => r.TranslationRoomCode).Distinct().Should().HaveCount(1);
        rooms.Should().OnlyContain(r => r.HostId == hostId);
        rooms.Select(r => r.SeriesOccurrenceLocalDate).Distinct().Should().HaveCount(14);
    }

    [Fact]
    public async Task The_horizon_rolls_forward_as_days_pass()
    {
        var response = await CreateDailyAsync(Guid.NewGuid(), "08:00", endDateLocal: "2026-09-05");
        response.MaterializedOccurrenceCount.Should().Be(14);

        // Three days pass. Nothing else changes.
        _now = _now.AddDays(3);

        using (var scope = _factory.Services.CreateScope())
        {
            var series = scope.ServiceProvider.GetRequiredService<ITranslationRoomSeriesService>();
            var created = await series.MaterializeDueOccurrencesAsync();
            created.Should().Be(3, "three new days came inside the 14-day horizon");
        }

        var rooms = await OccurrencesOfAsync(response.Series.SeriesId);
        rooms.Should().HaveCount(17);
        rooms.Last().ScheduledAt.Should().Be(new DateTime(2026, 8, 23, 1, 0, 0, DateTimeKind.Utc));

        // Idempotent: sweeping again on the same clock creates nothing and, critically, cannot
        // duplicate a day — the unique (series_id, occurrence_date) index makes that impossible.
        using (var scope = _factory.Services.CreateScope())
        {
            var series = scope.ServiceProvider.GetRequiredService<ITranslationRoomSeriesService>();
            (await series.MaterializeDueOccurrencesAsync()).Should().Be(0);
        }

        (await OccurrencesOfAsync(response.Series.SeriesId)).Should().HaveCount(17);
    }

    [Fact]
    public async Task A_series_stops_generating_once_it_reaches_its_end_date()
    {
        // The end condition, demonstrated rather than asserted in the abstract: this is what
        // stops an abandoned demo workspace producing rooms forever.
        var response = await CreateDailyAsync(Guid.NewGuid(), "08:00", endDateLocal: "2026-08-09");
        response.TotalOccurrenceCount.Should().Be(3);   // 07, 08, 09
        response.MaterializedOccurrenceCount.Should().Be(3);

        _now = _now.AddDays(60);

        using (var scope = _factory.Services.CreateScope())
        {
            var series = scope.ServiceProvider.GetRequiredService<ITranslationRoomSeriesService>();
            (await series.MaterializeDueOccurrencesAsync()).Should().Be(0);
        }

        (await OccurrencesOfAsync(response.Series.SeriesId)).Should().HaveCount(3);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
            var series = await db.TranslationRoomSeries.SingleAsync(s => s.Id == response.Series.SeriesId);
            series.Status.Should().Be(RecurrenceSeriesStatuses.Completed);
        }
    }

    [Fact]
    public async Task Cancelling_the_series_cancels_future_occurrences_and_stops_the_sweep()
    {
        var hostId = Guid.NewGuid();
        var response = await CreateDailyAsync(hostId, "08:00", endDateLocal: "2026-09-05");

        SetCaller(hostId);
        var cancel = await _client.PostAsync(
            $"/api/v1/translation-room-series/{response.Series.SeriesId}/cancel", content: null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK, await cancel.Content.ReadAsStringAsync());

        var cancelled = await cancel.Content.ReadFromJsonAsync<CancelSeriesResult>();
        cancelled!.CancelledOccurrenceCount.Should().Be(14);

        var rooms = await OccurrencesOfAsync(response.Series.SeriesId);
        rooms.Should().OnlyContain(r => r.Status == "CANCELLED");

        // And no more arrive, however much time passes.
        _now = _now.AddDays(10);
        using (var scope = _factory.Services.CreateScope())
        {
            var series = scope.ServiceProvider.GetRequiredService<ITranslationRoomSeriesService>();
            (await series.MaterializeDueOccurrencesAsync()).Should().Be(0);
        }
        (await OccurrencesOfAsync(response.Series.SeriesId)).Should().HaveCount(14);
    }

    [Fact]
    public async Task Cancelling_one_occurrence_does_not_kill_the_series()
    {
        var hostId = Guid.NewGuid();
        var response = await CreateDailyAsync(hostId, "08:00", endDateLocal: "2026-09-05");
        var rooms = await OccurrencesOfAsync(response.Series.SeriesId);
        var victim = rooms[5];

        SetCaller(hostId);
        var cancel = await _client.PostAsync($"/api/v1/translation-rooms/{victim.Id}/cancel", content: null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK, await cancel.Content.ReadAsStringAsync());

        // The series keeps going, and the sweep still extends it.
        _now = _now.AddDays(2);
        using (var scope = _factory.Services.CreateScope())
        {
            var series = scope.ServiceProvider.GetRequiredService<ITranslationRoomSeriesService>();
            (await series.MaterializeDueOccurrencesAsync()).Should().Be(2);
        }

        var after = await OccurrencesOfAsync(response.Series.SeriesId);
        after.Should().HaveCount(16);
        after.Count(r => r.Status == "CANCELLED").Should().Be(1);

        // The cancelled day is NOT regenerated: the watermark never moves backwards.
        after.Count(r => r.SeriesOccurrenceLocalDate == victim.SeriesOccurrenceLocalDate).Should().Be(1);
    }

    [Fact]
    public async Task Only_the_host_can_cancel_a_series()
    {
        var hostId = Guid.NewGuid();
        var response = await CreateDailyAsync(hostId, "08:00");

        SetCaller(Guid.NewGuid());
        var cancel = await _client.PostAsync(
            $"/api/v1/translation-room-series/{response.Series.SeriesId}/cancel", content: null);

        cancel.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_omitted_end_date_is_bounded_not_infinite()
    {
        var response = await CreateDailyAsync(Guid.NewGuid(), "08:00");

        response.TotalOccurrenceCount.Should().Be(RecurrenceLimits.DefaultDurationDays + 1);
        response.Series.EndDateLocal.Should().Be("2026-09-06"); // 2026-08-07 + 30 days
    }

    [Fact]
    public async Task A_request_carrying_both_a_one_off_time_and_a_repeat_rule_is_refused()
    {
        // Not silently resolved in favour of one of them: a silently discarded field on this
        // exact dialog is the bug WT-327 exists to remove.
        var request = new CreateTranslationRoomRequest(
            WorkspaceId: Guid.NewGuid(),
            Title: "Contradictory",
            Description: null,
            TranslationRoomType: "EVENT",
            MaxParticipants: null,
            SourceLanguage: "en-US",
            TargetLanguages: new List<string> { "vi-VN" },
            Settings: null,
            ScheduledAt: new DateTime(2026, 8, 20, 1, 0, 0, DateTimeKind.Utc),
            InvitedEmails: null,
            Recurrence: new RecurrenceRequest(RecurrenceTypes.Daily, "08:00", Hcm));

        SetCaller(Guid.NewGuid());
        var response = await _client.PostAsJsonAsync("/api/v1/translation-rooms", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("not both");
    }

    [Fact]
    public async Task The_shared_code_opens_todays_meeting_not_the_first_one()
    {
        // The point of one code per booking: following it on any day lands on that day's meeting.
        var hostId = Guid.NewGuid();
        var response = await CreateDailyAsync(hostId, "08:00", endDateLocal: "2026-09-05");

        var rooms = await OccurrencesOfAsync(response.Series.SeriesId);
        var code = rooms[0].TranslationRoomCode;
        rooms.Should().OnlyContain(r => r.TranslationRoomCode == code);

        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITranslationRoomRepository>();

        // Resolution is against the real clock, not the injected one — the repository has no
        // injected clock — so this asserts the RULE rather than a specific date: whatever it
        // resolves to is a real occurrence of this series, and it is the earliest one still ahead.
        var resolved = await repository.GetByCodeAsync(code);

        resolved.Should().NotBeNull();
        resolved!.SeriesId.Should().Be(response.Series.SeriesId);

        var stillAhead = rooms
            .Where(r => r.ScheduledAt >= DateTime.UtcNow)
            .OrderBy(r => r.ScheduledAt)
            .ToList();

        if (stillAhead.Count > 0)
        {
            resolved.Id.Should().Be(stillAhead[0].Id, "a shared code opens the next meeting due");
        }
        else
        {
            resolved.Id.Should().Be(
                rooms.OrderByDescending(r => r.ScheduledAt).First().Id,
                "once the series is behind us the code opens the most recent meeting, not nothing");
        }
    }

    [Fact]
    public async Task A_weekly_series_materialises_only_the_weekdays_it_names()
    {
        // Booked 2026-08-06 (a Thursday) at 10:00 local, so the rule starts tomorrow, the 7th.
        // Mondays and Wednesdays through 2026-09-05: Aug 10, 12, 17, 19, 24, 26, 31 and Sep 2.
        SetCaller(Guid.NewGuid());
        var response = await _client.PostAsJsonAsync("/api/v1/translation-rooms", BuildRequest(
            Guid.NewGuid(),
            new RecurrenceRequest(
                RecurrenceTypes.Weekly, "08:00", Hcm,
                StartDateLocal: null,
                EndDateLocal: "2026-09-05",
                ByWeekdays: new List<int> { 1, 3 })));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var created = (await response.Content.ReadFromJsonAsync<CreateRecurringRoomResponse>())!;

        created.Series.Type.Should().Be(RecurrenceTypes.Weekly);
        created.Series.ByWeekdays.Should().Equal(1, 3);
        created.Series.ByMonthDay.Should().BeNull();
        created.TotalOccurrenceCount.Should().Be(8);

        // The 14-day horizon reaches Aug 20, so only the first four exist yet — and, unlike a
        // daily series, "14 days" does NOT mean "14 rooms".
        created.MaterializedOccurrenceCount.Should().Be(4);

        var rooms = await OccurrencesOfAsync(created.Series.SeriesId);
        rooms.Select(r => r.ScheduledAt!.Value).Should().Equal(
            new DateTime(2026, 8, 10, 1, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 12, 1, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 17, 1, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 19, 1, 0, 0, DateTimeKind.Utc));

        // Never Tuesday. The failure this pins is a weekly series drifting into daily behaviour.
        rooms.Should().OnlyContain(r =>
            r.SeriesOccurrenceLocalDate!.Value.DayOfWeek == DayOfWeek.Monday ||
            r.SeriesOccurrenceLocalDate!.Value.DayOfWeek == DayOfWeek.Wednesday);
    }

    [Fact]
    public async Task A_monthly_series_whose_first_meeting_is_past_the_horizon_still_gets_its_room()
    {
        // "The 1st of every month", booked on the 6th: the first occurrence is 26 days out, well
        // past the 14-day horizon. The booking must still come back with a room the host can
        // share — a booking nobody can be invited to is not a booking — so the first occurrence
        // is materialised whatever the horizon says.
        SetCaller(Guid.NewGuid());
        var response = await _client.PostAsJsonAsync("/api/v1/translation-rooms", BuildRequest(
            Guid.NewGuid(),
            new RecurrenceRequest(
                RecurrenceTypes.Monthly, "08:00", Hcm,
                StartDateLocal: null,
                EndDateLocal: "2026-11-30",
                ByMonthDay: 1)));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var created = (await response.Content.ReadFromJsonAsync<CreateRecurringRoomResponse>())!;

        created.Series.Type.Should().Be(RecurrenceTypes.Monthly);
        created.Series.ByMonthDay.Should().Be(1);
        created.TotalOccurrenceCount.Should().Be(3); // Sep 1, Oct 1, Nov 1
        created.MaterializedOccurrenceCount.Should().Be(1);
        created.FirstOccurrence.ScheduledAt.Should().Be(new DateTime(2026, 9, 1, 1, 0, 0, DateTimeKind.Utc));

        // August has a 1st, but it is in the past — the rule must not reach backwards for it.
        var rooms = await OccurrencesOfAsync(created.Series.SeriesId);
        rooms.Should().HaveCount(1);

        // And the sweep resumes from that date rather than re-creating it.
        using (var scope = _factory.Services.CreateScope())
        {
            var series = scope.ServiceProvider.GetRequiredService<ITranslationRoomSeriesService>();
            (await series.MaterializeDueOccurrencesAsync()).Should().Be(0);
        }

        (await OccurrencesOfAsync(created.Series.SeriesId)).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_grouped_list_shows_one_booking_where_an_ungrouped_one_shows_every_occurrence()
    {
        // The defect this feature exists to fix: a daily standup filled the meetings list with
        // fourteen rows that were, to the person who booked it, one meeting.
        var hostId = Guid.NewGuid();
        var response = await CreateDailyAsync(hostId, "08:00", endDateLocal: "2026-09-05");
        response.MaterializedOccurrenceCount.Should().Be(14);

        var grouped = await ListAsync(groupBySeries: true);
        grouped.Rooms.Should().HaveCount(1);
        grouped.Total.Should().Be(1, "the count beside a collapsed row must count bookings, not occurrences");

        var row = grouped.Rooms[0];
        row.SeriesId.Should().Be(response.Series.SeriesId);
        row.Series.Should().NotBeNull();
        row.Series!.Type.Should().Be(RecurrenceTypes.Daily);
        row.Series.StartTimeLocal.Should().Be("08:00");
        row.Series.TimeZone.Should().Be(Hcm);
        row.Series.OccurrenceCount.Should().Be(14);

        // The row stands for the meeting the user would act on, and says so consistently: the
        // room it points at is the one its own "next" field names.
        row.Series.NextOccurrenceAt.Should().Be(row.ScheduledAt);
        row.ScheduledAt.Should().NotBeNull();

        // The home day panel asks the same endpoint without grouping, and still gets every day —
        // collapsing there would empty every date but one.
        var ungrouped = await ListAsync(groupBySeries: false);
        ungrouped.Rooms.Should().HaveCount(14);
        ungrouped.Total.Should().Be(14);
        ungrouped.Rooms.Should().OnlyContain(r => r.SeriesId == response.Series.SeriesId);
        ungrouped.Rooms.Should().OnlyContain(r => r.Series == null,
            "an ungrouped row is one occurrence and must not claim to be the whole booking");
    }

    [Fact]
    public async Task A_one_off_meeting_is_never_collapsed_by_grouping()
    {
        var hostId = Guid.NewGuid();
        await CreateDailyAsync(hostId, "08:00", endDateLocal: "2026-09-05");

        SetCaller(hostId);
        var oneOff = await _client.PostAsJsonAsync("/api/v1/translation-rooms", new CreateTranslationRoomRequest(
            WorkspaceId: Guid.NewGuid(),
            Title: "One-off review",
            Description: null,
            TranslationRoomType: "EVENT",
            MaxParticipants: null,
            SourceLanguage: "en-US",
            TargetLanguages: new List<string> { "en-US", "vi-VN" },
            Settings: null,
            // Real UtcNow, not the injected clock: only the series service reads the injected one,
            // and a one-off room's "must be in the future" check is against the real wall clock.
            ScheduledAt: DateTime.UtcNow.AddDays(1),
            InvitedEmails: null,
            Recurrence: null));
        oneOff.StatusCode.Should().Be(HttpStatusCode.Created, await oneOff.Content.ReadAsStringAsync());

        var grouped = await ListAsync(groupBySeries: true);
        grouped.Rooms.Should().HaveCount(2, "the booking collapses to one row; the one-off is untouched");
        grouped.Rooms.Should().ContainSingle(r => r.SeriesId == null && r.Series == null);
    }

    [Fact]
    public async Task The_booking_read_returns_its_rule_its_occurrences_and_the_one_to_join()
    {
        var hostId = Guid.NewGuid();
        var response = await CreateDailyAsync(hostId, "08:00", endDateLocal: "2026-09-05");

        SetCaller(hostId);
        var read = await _client.GetAsync($"/api/v1/translation-room-series/{response.Series.SeriesId}");
        read.StatusCode.Should().Be(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());

        var detail = (await read.Content.ReadFromJsonAsync<SeriesDetailResponse>())!;
        detail.Series.SeriesId.Should().Be(response.Series.SeriesId);
        detail.HostId.Should().Be(hostId);
        detail.Title.Should().Be("Daily standup");
        detail.Occurrences.Should().HaveCount(14);
        detail.Occurrences.Select(o => o.ScheduledAt).Should().BeInAscendingOrder();

        // The stable target a "join this booking" link resolves to.
        detail.CurrentOccurrenceId.Should().NotBeNull();
        detail.Occurrences.Should().Contain(o => o.Id == detail.CurrentOccurrenceId);
    }

    [Fact]
    public async Task A_stranger_cannot_read_a_booking_by_guessing_its_id()
    {
        // Before this read took a caller at all, [Authorize] was the entire check: any signed-in
        // user could read any workspace's schedule, title and host from an id.
        var response = await CreateDailyAsync(Guid.NewGuid(), "08:00", endDateLocal: "2026-09-05");

        SetCaller(Guid.NewGuid());
        var read = await _client.GetAsync($"/api/v1/translation-room-series/{response.Series.SeriesId}");

        read.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a refusal and a missing series must be indistinguishable, or the 403 confirms the id");
    }

    [Fact]
    public async Task Editing_the_booking_rewrites_the_meetings_still_to_come()
    {
        var hostId = Guid.NewGuid();
        var response = await CreateDailyAsync(hostId, "08:00", endDateLocal: "2026-09-05");

        SetCaller(hostId);
        var edit = await _client.PatchAsJsonAsync(
            $"/api/v1/translation-room-series/{response.Series.SeriesId}",
            new UpdateSeriesRequest(Title: "Standup (renamed)", Description: "Now with an agenda"));

        edit.StatusCode.Should().Be(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync());
        var result = (await edit.Content.ReadFromJsonAsync<UpdateSeriesResult>())!;
        result.UpdatedOccurrenceCount.Should().Be(14);

        // The template, so occurrences the worker has not created yet are stamped from the edit...
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
            var series = await db.TranslationRoomSeries.AsNoTracking()
                .FirstAsync(s => s.Id == response.Series.SeriesId);
            series.Title.Should().Be("Standup (renamed)");
        }

        // ...and the rooms that already exist.
        var rooms = await OccurrencesOfAsync(response.Series.SeriesId);
        rooms.Should().OnlyContain(r => r.Title == "Standup (renamed)");

        // A later occurrence inherits the edited template rather than the original one.
        _now = _now.AddDays(2);
        using (var scope = _factory.Services.CreateScope())
        {
            var series = scope.ServiceProvider.GetRequiredService<ITranslationRoomSeriesService>();
            (await series.MaterializeDueOccurrencesAsync()).Should().Be(2);
        }

        (await OccurrencesOfAsync(response.Series.SeriesId))
            .Should().OnlyContain(r => r.Title == "Standup (renamed)");
    }

    [Fact]
    public async Task Only_the_host_can_edit_a_booking()
    {
        var response = await CreateDailyAsync(Guid.NewGuid(), "08:00", endDateLocal: "2026-09-05");

        SetCaller(Guid.NewGuid());
        var edit = await _client.PatchAsJsonAsync(
            $"/api/v1/translation-room-series/{response.Series.SeriesId}",
            new UpdateSeriesRequest(Title: "Hijacked"));

        edit.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await OccurrencesOfAsync(response.Series.SeriesId))
            .Should().OnlyContain(r => r.Title == "Daily standup");
    }

    [Fact]
    public async Task A_one_off_room_is_completely_unaffected()
    {
        // The regression that would matter most: every room that is not part of a series must
        // look exactly as it always did.
        SetCaller(Guid.NewGuid());
        var request = new CreateTranslationRoomRequest(
            WorkspaceId: Guid.NewGuid(),
            Title: "Ordinary room",
            Description: null,
            TranslationRoomType: "EVENT",
            MaxParticipants: null,
            SourceLanguage: "en-US",
            TargetLanguages: new List<string> { "vi-VN" },
            Settings: null,
            ScheduledAt: null,
            InvitedEmails: null);

        var response = await _client.PostAsJsonAsync("/api/v1/translation-rooms", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var room = await response.Content.ReadFromJsonAsync<TranslationRoomDto>();
        room!.SeriesId.Should().BeNull();
        room.Status.Should().Be(RoomStatus.WAITING);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void SetCaller(Guid userId)
    {
        _client.DefaultRequestHeaders.Remove(TestAuthHandler.UserIdHeader);
        _client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId.ToString());
    }

    private static CreateTranslationRoomRequest BuildRequest(Guid workspaceId, RecurrenceRequest recurrence) =>
        new(
            WorkspaceId: workspaceId,
            Title: "Daily standup",
            Description: "Recurring",
            TranslationRoomType: "EVENT",
            MaxParticipants: null,
            SourceLanguage: "en-US",
            TargetLanguages: new List<string> { "en-US", "vi-VN" },
            Settings: null,
            ScheduledAt: null,
            InvitedEmails: null,
            Recurrence: recurrence);

    private async Task<CreateRecurringRoomResponse> CreateDailyAsync(
        Guid hostId, string timeLocal, string? endDateLocal = null)
    {
        SetCaller(hostId);
        var request = BuildRequest(Guid.NewGuid(),
            new RecurrenceRequest(RecurrenceTypes.Daily, timeLocal, Hcm, null, endDateLocal));

        var response = await _client.PostAsJsonAsync("/api/v1/translation-rooms", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<CreateRecurringRoomResponse>())!;
    }

    private async Task<TranslationRoomListResponse> ListAsync(bool groupBySeries)
    {
        var response = await _client.GetAsync(
            $"/api/v1/translation-rooms?pageSize=100&groupBySeries={groupBySeries.ToString().ToLowerInvariant()}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<TranslationRoomListResponse>())!;
    }

    private async Task<List<TranslationRoom>> OccurrencesOfAsync(Guid seriesId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
        return await db.TranslationRooms
            .AsNoTracking()
            .Where(r => r.SeriesId == seriesId)
            .OrderBy(r => r.ScheduledAt)
            .ToListAsync();
    }

    private static IConnectionMultiplexer BuildRedisStub()
    {
        var redis = new Mock<IConnectionMultiplexer>();
        var database = new Mock<IDatabase>();
        var subscriber = new Mock<ISubscriber>();

        database.Setup(d => d.StreamCreateConsumerGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<bool>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        database.Setup(d => d.StreamReadGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>())).ReturnsAsync(Array.Empty<StreamEntry>());
        database.Setup(d => d.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>())).ReturnsAsync(1L);
        database.Setup(d => d.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<NameValueEntry[]>(), It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>())).ReturnsAsync(new RedisValue("dummy-id"));
        database.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);

        subscriber.Setup(s => s.SubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>())).Returns(Task.CompletedTask);
        subscriber.Setup(s => s.UnsubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>())).Returns(Task.CompletedTask);
        subscriber.Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>())).ReturnsAsync(1L);

        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        redis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);
        return redis.Object;
    }
}
