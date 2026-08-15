using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Clients;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class PrometheusMetricsSourceTests
{
    private static (PrometheusMetricsSource Source, StubHandler Handler) Build(
        HttpStatusCode status,
        string body)
    {
        var handler = new StubHandler(status, body);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://prometheus.test/") };
        return (new PrometheusMetricsSource(client), handler);
    }

    [Fact]
    public async Task QueryAsync_ReadsLabelsAndValues()
    {
        var (source, handler) = Build(HttpStatusCode.OK, """
        {"status":"success","data":{"resultType":"vector","result":[
          {"metric":{"__name__":"up","job":"redis","instance":"redis-exporter:9121"},"value":[1755334800,"1"]},
          {"metric":{"__name__":"up","job":"rabbitmq","instance":"rabbitmq:15692"},"value":[1755334800,"0"]}
        ]}}
        """);

        var samples = await source.QueryAsync("up", CancellationToken.None);

        Assert.Equal(2, samples.Count);
        Assert.Equal("redis", samples[0].Label("job"));
        Assert.Equal(1, samples[0].Value);
        Assert.Equal(0, samples[1].Value);
        Assert.Equal("http://prometheus.test/api/v1/query?query=up", handler.LastRequestUri);
    }

    [Fact]
    public async Task QueryAsync_EscapesTheExpression()
    {
        // A raw '+' in a query string is a space to the server, and PromQL is full of characters
        // that mean something else in a URL.
        var (source, handler) = Build(HttpStatusCode.OK, EmptyVector);

        await source.QueryAsync("sum by (stage) (rate(x[1h]))", CancellationToken.None);

        Assert.NotNull(handler.LastRequestUri);
        Assert.DoesNotContain(" ", handler.LastRequestUri);
        Assert.Contains("sum%20by%20%28stage%29", handler.LastRequestUri);
    }

    [Fact]
    public async Task QueryAsync_ParsesNaN()
    {
        // Prometheus ships sample values as JSON STRINGS precisely so they can carry NaN and
        // +Inf, which JSON numbers cannot. histogram_quantile returns NaN routinely, and a parser
        // that treats it as 0 puts "0 ms" next to a stage that has no measurements.
        var (source, _) = Build(HttpStatusCode.OK, """
        {"status":"success","data":{"resultType":"vector","result":[
          {"metric":{"stage":"tts"},"value":[1755334800,"NaN"]}
        ]}}
        """);

        var samples = await source.QueryAsync("q", CancellationToken.None);

        Assert.True(double.IsNaN(samples[0].Value));
    }

    [Fact]
    public async Task QueryAsync_ParsesDecimalsUnderAnyAmbientCulture()
    {
        // vi-VN writes 0,5 for a half. Parsing "0.5" under it drops the point and yields 5 —
        // the exact shape of the billing bug this codebase has already shipped once.
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("vi-VN");
        try
        {
            var (source, _) = Build(HttpStatusCode.OK, """
            {"status":"success","data":{"resultType":"vector","result":[
              {"metric":{"stage":"stt"},"value":[1755334800,"2400.5"]}
            ]}}
            """);

            var samples = await source.QueryAsync("q", CancellationToken.None);

            Assert.Equal(2400.5, samples[0].Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task QueryAsync_TreatsAServerErrorAsUnavailable()
    {
        var (source, _) = Build(HttpStatusCode.ServiceUnavailable, "{}");

        await Assert.ThrowsAsync<PlatformMetricsUnavailableException>(
            () => source.QueryAsync("up", CancellationToken.None));
    }

    [Fact]
    public async Task QueryAsync_TreatsARejectedQueryAsTheCallersProblem_NotAnOutage()
    {
        // 400 means this one expression was wrong. Reporting it as unavailable would blank the
        // whole screen over one bad query.
        var (source, _) = Build(HttpStatusCode.BadRequest, """
        {"status":"error","errorType":"bad_data","error":"parse error"}
        """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.QueryAsync("up{", CancellationToken.None));

        Assert.Contains("parse error", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_TreatsAConnectionFailureAsUnavailable()
    {
        var client = new HttpClient(new ThrowingHandler(new HttpRequestException("refused")))
        {
            BaseAddress = new Uri("http://prometheus.test/"),
        };

        await Assert.ThrowsAsync<PlatformMetricsUnavailableException>(
            () => new PrometheusMetricsSource(client).QueryAsync("up", CancellationToken.None));
    }

    [Fact]
    public async Task ActiveAlertsAsync_ReadsNameSeveritySummaryAndStart()
    {
        var (source, handler) = Build(HttpStatusCode.OK, """
        {"status":"success","data":{"alerts":[
          {"labels":{"alertname":"WarpTalkAiWorkerMissing","severity":"critical"},
           "annotations":{"summary":"Required AI worker heartbeat is missing"},
           "state":"firing","activeAt":"2026-08-16T08:42:00Z"}
        ]}}
        """);

        var alerts = await source.ActiveAlertsAsync(CancellationToken.None);

        Assert.Equal("http://prometheus.test/api/v1/alerts", handler.LastRequestUri);
        Assert.Equal("WarpTalkAiWorkerMissing", alerts[0].Name);
        Assert.Equal("critical", alerts[0].Severity);
        Assert.Equal("firing", alerts[0].State);
        Assert.Equal(new DateTime(2026, 8, 16, 8, 42, 0, DateTimeKind.Utc), alerts[0].ActiveSince);
    }

    [Fact]
    public async Task ActiveAlertsAsync_SurvivesAnAlertWithNoAnnotations()
    {
        var (source, _) = Build(HttpStatusCode.OK, """
        {"status":"success","data":{"alerts":[
          {"labels":{"alertname":"Bare"},"state":"pending"}
        ]}}
        """);

        var alerts = await source.ActiveAlertsAsync(CancellationToken.None);

        Assert.Equal("Bare", alerts[0].Name);
        Assert.Equal("unknown", alerts[0].Severity);
        Assert.Null(alerts[0].Summary);
        Assert.Null(alerts[0].ActiveSince);
    }

    private const string EmptyVector =
        """{"status":"success","data":{"resultType":"vector","result":[]}}""";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public string? LastRequestUri { get; private set; }

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // AbsoluteUri, not ToString(): ToString() renders %20 back as a literal space for
            // display, so asserting on it cannot tell an escaped request from an unescaped one.
            LastRequestUri = request.RequestUri?.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => throw _exception;
    }
}
