using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Options;
using WarpTalk.BillingService.Infrastructure.Services;
using WarpTalk.BillingService.Infrastructure.Workers;
using WarpTalk.BillingService.Tests.Integration;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

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
    private ICartesiaSyncCoordinator _coordinator = null!;

    public CartesiaUsageSyncWorkerTests()
    {
        _repository
            .Setup(r => r.ReplaceWindowAsync(
                It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IReadOnlyCollection<ProviderUsageDaily>>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<string, DateOnly, DateOnly, IReadOnlyCollection<string>, IReadOnlyCollection<ProviderUsageDaily>, DateTime, CancellationToken>(
                (_, from, to, _, rows, _, _) => { lock (_writes) _writes.Add((from, to, rows)); })
            .Returns(Task.CompletedTask);
    }

    private CartesiaUsageSyncWorker Build(
        string? key = Key,
        string? keyId = ProductionKeyId,
        int interval = 10,
        FakeTime? time = null,
        ICartesiaSyncCoordinator? coordinator = null,
        CartesiaUsageSyncStatus? status = null,
        FakeHandler? http = null)
    {
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
        time ??= new FakeTime(Now);
        _status = status ?? new CartesiaUsageSyncStatus(options.Value.IsConfigured, options.Value.IsFilteredToApiKey);
        _coordinator = coordinator ?? new InProcessCartesiaSyncCoordinator(time);
        var handler = http ?? _http;
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(CartesiaUsageOptions.HttpClientName)).Returns(() => new HttpClient(handler));

        return new CartesiaUsageSyncWorker(
            services.BuildServiceProvider(),
            new CartesiaUsageClient(factory.Object, options),
            _status,
            _coordinator,
            options,
            _logger,
            time);
    }

    private static async Task<CartesiaUsageSyncResult> SyncAsync(CartesiaUsageSyncWorker worker)
        => (await worker.RunTurnAsync(CancellationToken.None)).Result!;

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

        var outcome = await worker.RunTurnAsync(CancellationToken.None);
        var result = outcome.Result!;

        result.Outcome.Should().Be(CartesiaUsageOutcome.Ok);
        result.Backfill.Should().BeTrue();
        result.FromDate.Should().Be(new DateOnly(2026, 8, 15), "35 UTC days, today included");
        result.ToDate.Should().Be(new DateOnly(2026, 9, 18));
        outcome.NextSyncIn.Should().Be(TimeSpan.FromMinutes(10));

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

        var state = await _status.GetAsync();
        state.Status.Should().Be("ok");
        state.LastSuccessAt.Should().Be(Now);
    }

    [Fact]
    public async Task LaterSyncs_ReadTheLastThreeDays_WaitForTheInterval_AndBackfillDaily()
    {
        var time = new FakeTime(Now);
        var worker = Build(keyId: null, time: time);
        for (var i = 0; i < 9; i++) _http.Enqueue(HttpStatusCode.OK, """{"data":[]}""");

        (await SyncAsync(worker)).Backfill.Should().BeTrue();

        // Not due yet: the turn is held until the interval has passed, and Cartesia is not called.
        time.Advance(TimeSpan.FromMinutes(5));
        (await worker.RunTurnAsync(CancellationToken.None)).Result.Should().BeNull();
        _http.Requests.Should().HaveCount(3);

        time.Advance(TimeSpan.FromMinutes(5));
        var recent = await SyncAsync(worker);
        time.Advance(TimeSpan.FromHours(24));
        var daily = await SyncAsync(worker);

        recent.Backfill.Should().BeFalse();
        recent.FromDate.Should().Be(new DateOnly(2026, 9, 16), "today and the two days before it");
        daily.Backfill.Should().BeTrue();
        _http.Requests.Should().HaveCount(9);
        _http.Requests.Should().NotContain(r => r.Uri.Query.Contains("api_key_id"), "no production key id is configured");
    }

    // ── Several replicas ────────────────────────────────────────────────────

    [Fact]
    public async Task ThreeReplicas_ShareOneSchedule_SoCartesiaSeesOneSyncPerInterval()
    {
        var time = new FakeTime(Now);
        var coordinator = new InProcessCartesiaSyncCoordinator(time);
        var status = new CartesiaUsageSyncStatus(configured: true, filteredToApiKey: true);
        var replicas = Enumerable.Range(0, 3).Select(_ => Build(time: time, coordinator: coordinator, status: status)).ToList();
        for (var i = 0; i < 30; i++) _http.Enqueue(HttpStatusCode.OK, """{"data":[]}""");

        // An hour of every replica polling every minute.
        var synced = 0;
        for (var minute = 0; minute < 60; minute++)
        {
            foreach (var replica in replicas)
            {
                if ((await replica.RunTurnAsync(CancellationToken.None)).Result is not null) synced++;
            }

            time.Advance(TimeSpan.FromMinutes(1));
        }

        synced.Should().Be(6, "one sync every 10 minutes, not one per replica");
        _http.Requests.Should().HaveCount(18);
        _writes.Count(w => w.From == new DateOnly(2026, 8, 15)).Should().Be(1, "one backfill across all replicas");
    }

    [Fact]
    public async Task ABackoffHoldsForEveryReplica_AndTheStreakCarriesAcrossThem()
    {
        var time = new FakeTime(Now);
        var coordinator = new InProcessCartesiaSyncCoordinator(time);
        var status = new CartesiaUsageSyncStatus(configured: true, filteredToApiKey: true);
        var first = Build(time: time, coordinator: coordinator, status: status);
        var second = Build(time: time, coordinator: coordinator, status: status);
        _http.Enqueue(HttpStatusCode.BadGateway, "");
        _http.Enqueue(HttpStatusCode.BadGateway, "");

        (await first.RunTurnAsync(CancellationToken.None)).NextSyncIn.Should().Be(TimeSpan.FromMinutes(10));
        time.Advance(TimeSpan.FromMinutes(9));
        (await second.RunTurnAsync(CancellationToken.None)).Result.Should().BeNull("the backoff is shared");
        time.Advance(TimeSpan.FromMinutes(1));

        // The other replica takes the next turn and keeps doubling rather than restarting at 10 min.
        (await second.RunTurnAsync(CancellationToken.None)).NextSyncIn.Should().Be(TimeSpan.FromMinutes(20));
        (await status.GetAsync()).FailureStreak.Should().Be(2);
        _http.Requests.Should().HaveCount(2);
    }

    [DockerFact]
    public async Task TheRedisLease_LetsOnlyOneReplicaSync_AndSharesTheStatus()
    {
        await using var redis = new ContainerBuilder()
            .WithImage("redis:7-alpine")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
            .Build();
        await redis.StartAsync();
        await using var connection = await ConnectionMultiplexer.ConnectAsync($"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}");

        var time = new FakeTime(Now);
        for (var i = 0; i < 3; i++) _http.Enqueue(HttpStatusCode.OK, """{"data":[]}""");
        var statuses = Enumerable.Range(0, 2)
            .Select(_ => new RedisCartesiaUsageSyncStatus(connection, true, true, NullLogger<RedisCartesiaUsageSyncStatus>.Instance))
            .ToList();
        // Two replicas: each its own coordinator and status object, sharing only the Redis server.
        var workers = statuses
            .Select(status => BuildWithStatus(time, new RedisCartesiaSyncCoordinator(connection, time), status))
            .ToList();

        var outcomes = await Task.WhenAll(workers.Select(worker => worker.RunTurnAsync(CancellationToken.None)));

        outcomes.Count(o => o.Result is not null).Should().Be(1, "the lease admits one replica");
        _http.Requests.Should().HaveCount(3);
        (await statuses[0].GetAsync()).Status.Should().Be("ok");
        (await statuses[1].GetAsync()).Status.Should().Be("ok", "the replica that did not sync reads the shared status");

        var ttl = await connection.GetDatabase().KeyTimeToLiveAsync(RedisCartesiaSyncCoordinator.TurnKey);
        ttl.Should().BeCloseTo(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30), "the lease is held until the next sync is due");
        (await connection.GetDatabase().KeyExistsAsync(RedisCartesiaSyncCoordinator.BackfillKey)).Should().BeTrue();
    }

    private CartesiaUsageSyncWorker BuildWithStatus(FakeTime time, ICartesiaSyncCoordinator coordinator, ICartesiaUsageSyncStatus status)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.ProviderUsageDaily).Returns(_repository.Object);
        var services = new ServiceCollection();
        services.AddSingleton(unitOfWork.Object);
        var options = Options.Create(new CartesiaUsageOptions { AdminApiKey = Key, UsageApiKeyId = ProductionKeyId });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(CartesiaUsageOptions.HttpClientName)).Returns(() => new HttpClient(_http));
        return new CartesiaUsageSyncWorker(
            services.BuildServiceProvider(), new CartesiaUsageClient(factory.Object, options), status, coordinator, options, _logger, time);
    }

    /// <summary>
    /// Multi-replica: the turn lease is not renewed while a sync runs, so a sync that outlived it
    /// would overlap with the next replica's turn and both would call Cartesia. The sync is cut off
    /// before the lease can expire — and the budget still fits three requests at the default timeout.
    /// </summary>
    [Fact]
    public void ASyncCanNeverOutliveTheTurnLease()
    {
        CartesiaUsageSyncWorker.SyncBudget.Should().BeLessThan(RedisCartesiaSyncCoordinator.TurnTimeout);
        (RedisCartesiaSyncCoordinator.TurnTimeout - CartesiaUsageSyncWorker.SyncBudget)
            .Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30));
        CartesiaUsageSyncWorker.SyncBudget.Should().BeGreaterThan(
            TimeSpan.FromSeconds(3 * new CartesiaUsageOptions().RequestTimeoutSeconds));
    }

    // ── Failures ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARejectedKey_WritesNothing_LogsOneLine_WaitsTheMaximum_AndNeverLogsTheKey()
    {
        var time = new FakeTime(Now);
        var worker = Build(time: time);
        _http.Enqueue(HttpStatusCode.Unauthorized, """{"error":"invalid key"}""");
        _http.Enqueue(HttpStatusCode.Unauthorized, """{"error":"invalid key"}""");

        var first = await worker.RunTurnAsync(CancellationToken.None);
        time.Advance(first.NextSyncIn);
        await worker.RunTurnAsync(CancellationToken.None);

        first.Result!.Outcome.Should().Be(CartesiaUsageOutcome.Unauthorized);
        _writes.Should().BeEmpty();
        first.NextSyncIn.Should().Be(TimeSpan.FromMinutes(60));
        var state = await _status.GetAsync();
        state.Status.Should().Be("error");
        state.Message.Should().Contain("HTTP 401").And.Contain("CARTESIA_ADMIN_API_KEY");
        _logger.Lines.Where(l => l.Level == LogLevel.Warning).Should().ContainSingle("one line per failure streak");
        _logger.Lines.Should().NotContain(l => l.Message.Contains(Key));
    }

    [Fact]
    public async Task RateLimited_HonoursRetryAfter()
    {
        var worker = Build();
        _http.Enqueue(HttpStatusCode.TooManyRequests, "{}", retryAfterSeconds: 300);

        var outcome = await worker.RunTurnAsync(CancellationToken.None);

        outcome.Result!.Outcome.Should().Be(CartesiaUsageOutcome.RateLimited);
        outcome.NextSyncIn.Should().Be(TimeSpan.FromMinutes(5));
        (await _status.GetAsync()).Message.Should().Contain("429");
    }

    [Fact]
    public async Task ServerErrors_BackOffByDoubling_UpToAnHour_AndRecoveryIsLoggedOnce()
    {
        var time = new FakeTime(Now);
        var worker = Build(time: time);
        var delays = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            _http.Enqueue(HttpStatusCode.BadGateway, "");
            var outcome = await worker.RunTurnAsync(CancellationToken.None);
            delays.Add(outcome.NextSyncIn);
            time.Advance(outcome.NextSyncIn);
        }

        delays.Should().Equal(
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(40),
            TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60));
        _logger.Lines.Count(l => l.Level == LogLevel.Warning).Should().Be(1);

        for (var i = 0; i < 3; i++) _http.Enqueue(HttpStatusCode.OK, """{"data":[]}""");
        (await worker.RunTurnAsync(CancellationToken.None)).NextSyncIn.Should().Be(TimeSpan.FromMinutes(10));
        _logger.Lines.Should().ContainSingle(l => l.Message.Contains("recovered after 5 failed attempt"));
        (await _status.GetAsync()).Status.Should().Be("ok");
    }

    [Fact]
    public async Task AnExceptionInTheLoop_DoesNotStopTheHost()
    {
        var worker = Build();
        _http.Throw(new HttpRequestException("connection refused"));

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await _status.GetAsync()).Status == "error");
        await worker.StopAsync(CancellationToken.None);

        worker.ExecuteTask!.IsFaulted.Should().BeFalse("an escaping exception would stop the whole billing host");
        (await _status.GetAsync()).Message.Should().Be("Cartesia usage sync failed (HttpRequestException)");
        _logger.Lines.Should().NotContain(l => l.Message.Contains(Key));
    }

    [Fact]
    public async Task ACoordinatorFailure_SkipsTheTurn_AndDoesNotStopTheHost()
    {
        var coordinator = new Mock<ICartesiaSyncCoordinator>();
        coordinator
            .Setup(c => c.TryBeginTurnAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "redis down"));
        var worker = Build(coordinator: coordinator.Object);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(_logger.Lines.Any(l => l.Message.Contains("could not coordinate"))));
        await worker.StopAsync(CancellationToken.None);

        worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        _http.Requests.Should().BeEmpty("without the lease no replica calls Cartesia");
    }

    // ── Not configured ──────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutAnAdminKey_TheWorkerIsDisabled_LogsOnce_AndCallsNothing()
    {
        var worker = Build(key: null);

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;
        await worker.StopAsync(CancellationToken.None);

        var state = await _status.GetAsync();
        state.Status.Should().Be("disabled");
        state.Message.Should().Contain("CARTESIA_ADMIN_API_KEY is not set");
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

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200 && !await condition(); i++) await Task.Delay(25);
        (await condition()).Should().BeTrue();
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
            lock (this) return Send(request);
        }

        private Task<HttpResponseMessage> Send(HttpRequestMessage request)
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
