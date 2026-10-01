using FluentAssertions;
using WarpTalk.BillingService.Application.Services;

namespace WarpTalk.BillingService.Tests.Application.Providers;

/// <summary>Shapes captured from status.livekit.io (Statuspage) and status.openai.com (incident.io) on 2026-09-25.</summary>
public sealed class StatusPageParserTests
{
    [Fact]
    public void The_indicator_and_description_are_read()
    {
        var status = StatusPageParser.ParseStatus(
            """{"page":{"id":"1l77jzt47dnl","name":"LiveKit"},"status":{"indicator":"minor","description":"Partially Degraded Service"}}""");

        status.Indicator.Should().Be("minor");
        status.Description.Should().Be("Partially Degraded Service");
    }

    [Fact]
    public void Incidents_take_started_at_else_created_at_and_keep_only_https_links()
    {
        var incidents = StatusPageParser.ParseIncidents("livekit", """
            {"incidents":[
              {"id":"wrg1yfdmxm0g","name":"Increased API latency","status":"postmortem","impact":"minor",
               "created_at":"2026-09-15T15:10:33.000-07:00","started_at":"2026-09-15T15:10:33.000-07:00",
               "resolved_at":"2026-09-15T16:15:15.748-07:00","shortlink":"https://stspg.io/5pryg1hfzn2y"},
              {"id":"01M3ACKDE4GYY67FXXNRRSC0CE","name":"Elevated Error Rates","status":"identified","impact":"none",
               "created_at":"2026-09-24T18:59:44Z","started_at":null,"resolved_at":null,"shortlink":"javascript:alert(1)"}
            ]}
            """);

        incidents.Should().HaveCount(2);
        incidents[0].StartedAt.Should().Be(new DateTime(2026, 9, 15, 22, 10, 33, DateTimeKind.Utc));
        incidents[0].ResolvedAt.Should().NotBeNull();
        incidents[0].Url.Should().Be("https://stspg.io/5pryg1hfzn2y");
        incidents[1].StartedAt.Should().Be(new DateTime(2026, 9, 24, 18, 59, 44, DateTimeKind.Utc));
        incidents[1].ResolvedAt.Should().BeNull();
        incidents[1].Url.Should().BeNull();
    }

    [Fact]
    public void A_resolved_incident_with_no_resolution_time_is_dropped_rather_than_shown_open()
        => StatusPageParser.ParseIncidents("openai",
                """{"incidents":[{"id":"x","name":"n","status":"resolved","impact":"major","created_at":"2026-09-01T00:00:00Z","resolved_at":null}]}""")
            .Should().BeEmpty();

    [Fact]
    public void A_page_without_the_expected_shape_yields_nothing()
    {
        StatusPageParser.ParseStatus("{}").Indicator.Should().BeNull();
        StatusPageParser.ParseIncidents("openai", "{\"incidents\":{}}").Should().BeEmpty();
    }
}
