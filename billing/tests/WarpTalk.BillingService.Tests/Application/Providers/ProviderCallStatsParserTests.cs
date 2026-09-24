using FluentAssertions;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Services;

namespace WarpTalk.BillingService.Tests.Application.Providers;

/// <summary>
/// The Redis layout is a cross-repo contract with warptalk-ai shared/provider_calls.py; the Python
/// side pins the same field strings in tests/test_provider_calls.py.
/// </summary>
public sealed class ProviderCallStatsParserTests
{
    private static readonly DateOnly Day = new(2026, 9, 25);

    private static KeyValuePair<string, long> F(string field, long value) => new(field, value);

    [Fact]
    public void Fields_of_one_hour_operation_and_model_become_one_row()
    {
        var rows = ProviderCallStatsParser.Parse(Day,
        [
            F("openai|07|translation|gpt-4.1|ok", 40),
            F("openai|07|translation|gpt-4.1|rate_limited", 3),
            F("openai|07|translation|gpt-4.1|client_error", 1),
            F("openai|07|translation|gpt-4.1|lat:500", 30),
            F("openai|07|translation|gpt-4.1|lat:1000", 13),
            F("openai|07|translation|gpt-4.1|lat_sum", 21000),
        ]);

        var row = rows.Should().ContainSingle().Subject;
        row.Provider.Should().Be("openai");
        row.HourStart.Should().Be(new DateTime(2026, 9, 25, 7, 0, 0, DateTimeKind.Utc));
        row.Operation.Should().Be("translation");
        row.Model.Should().Be("gpt-4.1");
        row.Ok.Should().Be(40);
        row.RateLimited.Should().Be(3);
        row.ClientError.Should().Be(1);
        row.LatencyCount.Should().Be(43);
        row.LatencySumMs.Should().Be(21000);
        ProviderCallStatMerge.ParseBuckets(row.LatencyBuckets).Should().BeEquivalentTo(new Dictionary<string, long> { ["500"] = 30, ["1000"] = 13 });
    }

    [Fact]
    public void Every_outcome_of_the_python_vocabulary_has_a_column()
    {
        var outcomes = new[] { "ok", "quota", "rate_limited", "auth", "client_error", "server_error", "timeout", "network_error", "error" };
        var row = ProviderCallStatsParser.Parse(Day, outcomes.Select(o => F($"cartesia|00|tts|sonic-3.5|{o}", 1))).Single();

        ProviderCallStatMerge.Calls(row).Should().Be(outcomes.Length);
        ProviderCallStatMerge.Failures(row).Should().Be(7, "ok and client_error do not count against the provider");
    }

    [Fact]
    public void A_field_that_does_not_fit_is_skipped_not_fatal()
    {
        var rows = ProviderCallStatsParser.Parse(Day,
        [
            F("garbage", 1),
            F("openai|25|stt|-|ok", 1),
            F("openai|xx|stt|-|ok", 1),
            F("openai|03|stt|-|a_new_outcome", 9),
            F("openai|03|stt|-|ok", 2),
        ]);

        rows.Should().ContainSingle().Which.Ok.Should().Be(2);
    }

    [Fact]
    public void The_key_is_the_utc_day()
        => ProviderCallStatsParser.KeyFor(Day).Should().Be("warptalk:provider_calls:2026-09-25");

    [Fact]
    public void A_reread_only_ever_raises_a_counter()
    {
        var stored = new ProviderCallStat { Ok = 50, Quota = 2, LatencyCount = 52, LatencySumMs = 9000, LatencyBuckets = "{\"250\":52}" };
        // Redis lost the hash and started again: a smaller reading must not erase the hour.
        var smaller = new ProviderCallStat { Ok = 5, Quota = 0, LatencyCount = 5, LatencySumMs = 400, LatencyBuckets = "{\"100\":5}" };

        ProviderCallStatMerge.RaiseTo(stored, smaller).Should().BeFalse();
        stored.Ok.Should().Be(50);
        stored.LatencyCount.Should().Be(52);

        var larger = new ProviderCallStat { Ok = 60, Quota = 2, LatencyCount = 62, LatencySumMs = 11000, LatencyBuckets = "{\"250\":62}" };
        ProviderCallStatMerge.RaiseTo(stored, larger).Should().BeTrue();
        stored.Ok.Should().Be(60);
        stored.LatencySumMs.Should().Be(11000);
        stored.LatencyBuckets.Should().Be("{\"250\":62}");
    }

    [Fact]
    public void A_malformed_histogram_is_empty_not_an_exception()
        => ProviderCallStatMerge.ParseBuckets("{not json").Should().BeEmpty();
}
