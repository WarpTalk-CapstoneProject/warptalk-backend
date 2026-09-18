using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Options;
using WarpTalk.BillingService.Infrastructure.Services;
using WarpTalk.BillingService.Infrastructure.Workers;

namespace WarpTalk.BillingService.Tests.Infrastructure.Workers;

/// <summary>
/// The Cartesia usage sync against a fake HTTP handler: what it asks Cartesia, what it writes, how it
/// backs off, that it never logs the key, and that nothing it hits can stop the billing host.
/// </summary>
public sealed class CartesiaUsageSyncWorkerTests
{
    // A placeholder, not a key: the tests only need a recognisable string to look for in the logs.
    private const string Key = "test-admin-key-placeholder-0001";
    private const string ProductionKeyId = "00000000-0000-4000-8000-000000000001";

    private static readonly DateTime Now = new(2026, 9, 18, 9, 30, 0, DateTimeKind.Utc);

    private readonly FakeHandler _http = new();
    private readonly CapturingLogger<CartesiaUsageSyncWorker> _logger = new();
    private readonly Mock<IProviderUsageDailyRepository> _repository = new();
    private readonly List<(DateOnly From, DateOnly To, IReadOnlyCollection<ProviderUsageDaily> Rows)> _writes = new();
    private CartesiaUsageSyncStatus _status = null!;

    private CartesiaUsageSyncWorker Build(string? key = Key, string? keyId = ProductionKeyId, int interval = 10, FakeTime? time = null)
    {
        _repository
            .Setup(r => r.ReplaceWindowAsync(
                It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IReadOnlyCollection<ProviderUsageDaily>>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<string, DateOnly, DateOnly, IReadOnlyCollection<string>, IReadOnlyCollection<ProviderUsageDaily>, DateTime, CancellationToken>(
                (_, from, to, _, rows, _, _) => _writes.Add((from, to, rows)))
            .Returns(Task.CompletedTask);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.ProviderUsageDaily).Returns(_repository.Object);

        var services = new ServiceCollection();
        services.AddSingleton(unitOfWork.Object);

        var options = Options.Create(new CartesiaUsageOptions
        {
            AdminApiKey = key,
            UsageApiKeyId = keyId,
            UsageSyncIntervalMinutes = interval,
        });
        _status = new CartesiaUsageSyncStatus(options.Value.IsConfigured, options.Value.IsFilteredToApiKey);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(CartesiaUsageOptions.HttpClientName)).Returns(() => new HttpClient(_http));

        return new CartesiaUsageSyncWorker(
            services.BuildServiceProvider(),
            new CartesiaUsageClient(factory.Object, options),
            _status,
            options,
            _logger,
            time ?? new FakeTime(Now));
    }

    // ── A good sync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FirstSync_BackfillsThirtyFiveUtcDays_FromTotalsCapabilitiesAndModels()
    {
        var worker = Build();
        _http.Enqueue(HttpStatusCode.OK, """
            {"data":[
              {"start_ts":"2026-09-17T00:00:00.000Z","end_ts":"2026-09-18T00:00:00.000Z","credits":1200},
              {"start_ts":"2026-09-18T00:00:00.000Z","end_ts":"2026-09-19T00:00:00.000Z","credits":300}]}
            """);
        _http.Enqueue(HttpStatusCode.OK, """
            {"group_by":"capability","data":[
              {"id":"tts","label":"Text to speech","buckets":[
                {"start_ts":"2026-09-17T00:00:00.000Z","end_ts":"2026-09-18T00:00:00.000Z","credits":1100},
                {"start_ts":"2026-09-18T00:00:00.000Z","end_ts":"2026-09-19T00:00:00.000Z","credits":300}]},
              {"id":"stt","buckets":[
                {"start_ts":"2026-09-17T00:00:00.000Z","end_ts":"2026-09-18T00:00:00.000Z","credits":100},
                {"start_ts":"2026-09-18T00:00:00.000Z","end_ts":"2026-09-19T00:00:00.000Z","credits":0}]}]}
            """);
        _http.Enqueue(HttpStatusCode.OK, """
            {"group_by":"model","data":[
              {"id":"sonic-3.5","buckets":[{"start_ts":"2026-09-18T00:00:00.000Z","end_ts":"2026-09-19T00:00:00.000Z","credits":300}]}]}
            """);

        var result = await worker.SyncOnceAsync(CancellationToken.None);

        result.Outcome.Should().Be(CartesiaUsageOutcome.Ok);
        result.Backfill.Should().BeTrue();
        result.FromDate.Should().Be(new DateOnly(2026, 8, 15), "35 UTC days, today included");
        result.ToDate.Should().Be(new DateOnly(2026, 9, 18));

        // What was asked: one ungrouped and two grouped day-interval reads of the same window, for the
        // production key only, with the version header and the key only in Authorization.
        _http.Requests.Select(r => r.Uri.AbsolutePath).Should().AllBe("/usage/credits");
        _http.Requests[0].Uri.Query.Should().Be(
            "?start_ts=2026-08-15T00%3A00%3A00Z&end_ts=2026-09-19T00%3A00%3A00Z&interval=day&api_key_id=" + ProductionKeyId);
        _http.Requests[1].Uri.Query.Should().Contain("group_by=capability");
        _http.Requests[2].Uri.Query.Should().Contain("group_by=model");
        _http.Requests.Should().OnlyContain(r => r.Version == "2026-08-14" && r.Authorization == "Bearer " + Key);

        // What was written: a total for every day (0 where Cartesia reported nothing), and a group row
        // only where the group used credits.
        var rows = _writes.Should().ContainSingle().Subject.Rows;
        rows.Where(r => r.GroupKind == "total").Should().HaveCount(35);
        rows.Single(r => r.GroupKind == "total" && r.UsageDate == new DateOnly(2026, 9, 17)).Credits.Should().Be(1200);
        rows.Single(r => r.GroupKind == "total" && r.UsageDate == new DateOnly(2026, 9, 1)).Credits.Should().Be(0);
        rows.Where(r => r.GroupKind != "total").Select(r => (r.GroupKind, r.GroupId, r.UsageDate.Day, r.Credits))
            .Should().BeEquivalentTo(new[]
            {
                ("capability", "tts", 17, 1100L), ("capability", "tts", 18, 300L), ("capability", "stt", 17, 100L),
                ("model", "sonic-3.5", 18, 300L),
            });
        rows.Single(r => r.GroupId == "tts" && r.UsageDate.Day == 17).GroupLabel.Should().Be("Text to speech");

        _status.Current.Status.Should().Be("ok");
        _status.Current.LastSuccessAt.Should().Be(Now);
        worker.AfterSync(result).Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task LaterSyncs_ReadTheLastThreeDays_UntilTheDailyBackfillIsDue()
    {
        var time = new FakeTime(Now);
        var worker = Build(keyId: null, time: time);
        for (var i = 0; i < 9; i++) _http.Enqueue(HttpStatusCode.OK, """{"data":[]}""");

        (await worker.SyncOnceAsync(CancellationToken.None)).Backfill.Should().BeTrue();
        time.Advance(TimeSpan.FromMinutes(10));
        var recent = await worker.SyncOnceAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromHours(24));
        var daily = await worker.SyncOnceAsync(CancellationToken.None);

        recent.Backfill.Should().BeFalse();
        recent.FromDate.Should().Be(new DateOnly(2026, 9, 16), "today and the two days before it");
        daily.Backfill.Should().BeTrue();
        _http.Requests.Should().NotContain(r => r.Uri.Query.Contains("api_key_id"), "no production key id is configured");
    }

    // ── Failures ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARejectedKey_WritesNothing_LogsOneLine_WaitsTheMaximum_AndNeverLogsTheKey()
    {
        var worker = Build();
        _http.Enqueue(HttpStatusCode.Unauthorized, """{"error":"invalid key"}""");
        _http.Enqueue(HttpStatusCode.Unauthorized, """{"error":"invalid key"}""");

        var first = await worker.SyncOnceAsync(CancellationToken.None);
        var firstDelay = worker.AfterSync(first);
        var second = await worker.SyncOnceAsync(CancellationToken.None);
        worker.AfterSync(second);

        first.Outcome.Should().Be(CartesiaUsageOutcome.Unauthorized);
        _writes.Should().BeEmpty();
        firstDelay.Should().Be(TimeSpan.FromMinutes(60));
        _status.Current.Status.Should().Be("error");
        _status.Current.Message.Should().Contain("HTTP 401").And.Contain("CARTESIA_ADMIN_API_KEY");
        _logger.Lines.Where(l => l.Level == LogLevel.Warning).Should().ContainSingle("one line per failure streak");
        _logger.Lines.Should().NotContain(l => l.Message.Contains(Key));
    }

    [Fact]
    public async Task RateLimited_HonoursRetryAfter()
    {
        var worker = Build();
        _http.Enqueue(HttpStatusCode.TooManyRequests, "{}", retryAfterSeconds: 300);

        var result = await worker.SyncOnceAsync(CancellationToken.None);

        result.Outcome.Should().Be(CartesiaUsageOutcome.RateLimited);
        worker.AfterSync(result).Should().Be(TimeSpan.FromMinutes(5));
        _status.Current.Message.Should().Contain("429");
    }

    [Fact]
    public async Task ServerErrors_BackOffByDoubling_UpToAnHour_AndRecoveryIsLoggedOnce()
    {
        var worker = Build();
        var delays = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            _http.Enqueue(HttpStatusCode.BadGateway, "");
            delays.Add(worker.AfterSync(await worker.SyncOnceAsync(CancellationToken.None)));
        }

        delays.Should().Equal(
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(40),
            TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60));
        _logger.Lines.Count(l => l.Level == LogLevel.Warning).Should().Be(1);

        for (var i = 0; i < 3; i++) _http.Enqueue(HttpStatusCode.OK, """{"data":[]}""");
        worker.AfterSync(await worker.SyncOnceAsync(CancellationToken.None)).Should().Be(TimeSpan.FromMinutes(10));
        _logger.Lines.Should().ContainSingle(l => l.Message.Contains("recovered after 5 failed attempt"));
        _status.Current.Status.Should().Be("ok");
    }

    [Fact]
    public async Task AnExceptionInTheLoop_DoesNotStopTheHost()
    {
        var worker = Build();
        _http.Throw(new HttpRequestException("connection refused"));

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _status.Current.Status == "error");
        await worker.StopAsync(CancellationToken.None);

        worker.ExecuteTask!.IsFaulted.Should().BeFalse("an escaping exception would stop the whole billing host");
        _status.Current.Message.Should().Be("Cartesia usage sync failed (HttpRequestException)");
        _logger.Lines.Should().NotContain(l => l.Message.Contains(Key));
    }

    // ── Not configured ──────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutAnAdminKey_TheWorkerIsDisabled_LogsOnce_AndCallsNothing()
    {
        var worker = Build(key: null);

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;
        await worker.StopAsync(CancellationToken.None);

        _status.Current.Status.Should().Be("disabled");
        _status.Current.Message.Should().Contain("CARTESIA_ADMIN_API_KEY is not set");
        _http.Requests.Should().BeEmpty();
        _writes.Should().BeEmpty();
        _logger.Lines.Should().ContainSingle(l => l.Message.Contains("is disabled"));
    }

    [Fact]
    public void Parse_ReadsBothDocumentedShapes()
    {
        using var flat = System.Text.Json.JsonDocument.Parse(
            """{"data":[{"start_ts":"2026-01-01T00:00:00.000Z","end_ts":"2026-01-02T00:00:00.000Z","credits":1200}]}""");
        using var grouped = System.Text.Json.JsonDocument.Parse(
            """{"group_by":"model","data":[{"id":"sonic-3.5","label":null,"buckets":[{"start_ts":"2026-01-01T00:00:00Z","end_ts":"2026-01-02T00:00:00Z","credits":7}]}]}""");

        CartesiaUsageClient.Parse(flat.RootElement).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Id = "all", Label = (string?)null });
        var model = CartesiaUsageClient.Parse(grouped.RootElement).Should().ContainSingle().Subject;
        model.Id.Should().Be("sonic-3.5");
        model.Buckets.Single().Credits.Should().Be(7);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        condition().Should().BeTrue();
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    private sealed record SeenRequest(Uri Uri, string? Authorization, string? Version);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        private Exception? _throw;

        public List<SeenRequest> Requests { get; } = new();

        public void Enqueue(HttpStatusCode status, string body, int? retryAfterSeconds = null)
            => _responses.Enqueue(() =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                if (retryAfterSeconds is { } seconds)
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
                return response;
            });

        public void Throw(Exception exception) => _throw = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SeenRequest(
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Cartesia-Version", out var values) ? values.Single() : null));
            if (_throw is not null) throw _throw;
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()()
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private sealed class FakeTime(DateTime now) : TimeProvider
    {
        private DateTime _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => new(_now);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " " + exception)));
        }
    }
}
