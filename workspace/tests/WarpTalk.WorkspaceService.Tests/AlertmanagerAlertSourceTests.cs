using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Infrastructure;
using WarpTalk.WorkspaceService.Infrastructure.Clients;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class AlertmanagerAlertSourceTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (AlertmanagerAlertSource Source, StubHandler Handler) Build(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://alertmanager.test:9093/") };
        return (new AlertmanagerAlertSource(client), handler);
    }

    [Fact]
    public async Task AsksOnlyForFiringAlertsThatAreNeitherSilencedNorInhibited()
    {
        var (source, handler) = Build(HttpStatusCode.OK, "[]");

        await source.FiringAlertsAsync(CancellationToken.None);

        Assert.Equal(
            "http://alertmanager.test:9093/api/v2/alerts?active=true&silenced=false&inhibited=false&unprocessed=false",
            handler.LastRequestUri);
    }

    [Fact]
    public async Task ReadsNameSeveritySummaryAndStart_AndDropsTheHeartbeatAlerts()
    {
        var (source, _) = Build(HttpStatusCode.OK, """
        [
          {"labels":{"alertname":"Watchdog","severity":"none"},"annotations":{},"startsAt":"2026-09-19T11:00:00Z","status":{"state":"active"}},
          {"labels":{"alertname":"InfoInhibitor","severity":"none"},"annotations":{},"startsAt":"2026-09-19T11:00:00Z","status":{"state":"active"}},
          {"labels":{"alertname":"WarpTalkNodeDiskFilling","severity":"warning"},
           "annotations":{"summary":"App node / is 84% full"},
           "startsAt":"2026-09-24T08:42:00.123Z","status":{"state":"active"}}
        ]
        """);

        var alerts = await source.FiringAlertsAsync(CancellationToken.None);

        var alert = Assert.Single(alerts);
        Assert.Equal("WarpTalkNodeDiskFilling", alert.Name);
        Assert.Equal("warning", alert.Severity);
        Assert.Equal("firing", alert.State);
        Assert.Equal("App node / is 84% full", alert.Summary);
        Assert.Equal(new DateTime(2026, 9, 24, 8, 42, 0, 123, DateTimeKind.Utc), alert.ActiveSince);
    }

    [Fact]
    public async Task AServerErrorThrows_SoTheScreenCanSayAlertsWereNotRead()
    {
        var (source, _) = Build(HttpStatusCode.ServiceUnavailable, "");

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FiringAlertsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("/grafana", "/grafana")]
    [InlineData("/grafana/", "/grafana")]
    [InlineData(" /grafana ", "/grafana")]
    [InlineData("https://grafana.example.com", null)]
    [InlineData("//evil.example.com/grafana", null)]
    [InlineData("/", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheGrafanaEmbedPathMustBeSameOrigin(string? configured, string? expected)
    {
        Assert.Equal(expected, DependencyInjection.NormalizeEmbedPath(configured));
    }
}
