using System;
using System.Threading.Tasks;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.TranscriptService.Application.Services;
using Xunit;

namespace WarpTalk.TranscriptService.Tests;

/// <summary>
/// PO 2026-10-02: "Loading into WarpBot knowledgebase…" must be a real state. These pin how the
/// per-glossary counters become idle / loading / stalled / failed / ready, and that only results
/// for jobs this service published are counted.
/// </summary>
public class GlossaryWarpBotStatusTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid GlossaryId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid TermId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private static GlossaryWarpBotStatus.Counters Counters(long requested, long indexed, long failed, long blocked, DateTime? updatedAt = null) =>
        new(requested, indexed, failed, blocked, updatedAt ?? Now);

    [Fact]
    public void Nothing_sent_is_idle_not_ready()
    {
        var status = GlossaryWarpBotStatus.Compute(Counters(0, 0, 0, 0, null), 0, Now);
        Assert.Equal("idle", status.State);
    }

    [Fact]
    public void Outstanding_results_are_loading_with_the_pending_count()
    {
        var status = GlossaryWarpBotStatus.Compute(Counters(10, 6, 1, 0), 1, Now);
        Assert.Equal("loading", status.State);
        Assert.Equal(3, status.Pending);
    }

    [Fact]
    public void All_results_in_and_none_failing_is_ready_and_blocked_is_not_a_failure()
    {
        var status = GlossaryWarpBotStatus.Compute(Counters(10, 8, 0, 2), 0, Now);
        Assert.Equal("ready", status.State);
        Assert.Equal(0, status.Pending);
        Assert.Equal(2, status.Blocked);
    }

    [Fact]
    public void All_results_in_with_a_term_still_failing_is_failed()
    {
        var status = GlossaryWarpBotStatus.Compute(Counters(10, 9, 1, 0), 1, Now);
        Assert.Equal("failed", status.State);
        Assert.Equal(1, status.Failed);
    }

    [Fact]
    public void A_failure_result_whose_term_was_later_indexed_no_longer_fails_the_glossary()
    {
        // Two requests for the same term: the first failed, the retry was indexed.
        var status = GlossaryWarpBotStatus.Compute(Counters(2, 1, 1, 0), 0, Now);
        Assert.Equal("ready", status.State);
    }

    [Fact]
    public void Results_outstanding_with_no_movement_past_the_limit_are_stalled_not_loading_forever()
    {
        var quiet = Now - GlossaryWarpBotStatus.StallAfter - TimeSpan.FromSeconds(1);
        Assert.Equal("stalled", GlossaryWarpBotStatus.Compute(Counters(10, 4, 0, 0, quiet), 0, Now).State);
        // Settled batches never stall, however old.
        Assert.Equal("ready", GlossaryWarpBotStatus.Compute(Counters(10, 10, 0, 0, quiet), 0, Now).State);
    }

    [Fact]
    public async Task A_result_for_a_job_this_service_did_not_publish_is_not_counted()
    {
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);

        Assert.False(await GlossaryWarpBotStatus.RecordResultAsync(db, "someone-elses-job", "indexed"));
        await db.DidNotReceiveWithAnyArgs().HashIncrementAsync(default, default, 1L, default);
    }

    [Fact]
    public async Task An_unknown_status_is_not_counted()
    {
        var db = Substitute.For<IDatabase>();
        Assert.False(await GlossaryWarpBotStatus.RecordResultAsync(db, "job", "queued"));
        await db.DidNotReceiveWithAnyArgs().StringGetAsync(default(RedisKey), default);
    }

    [Fact]
    public async Task A_failed_result_counts_once_and_marks_the_term_as_failing()
    {
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync((RedisKey)GlossaryWarpBotStatus.JobKey("job-1"), Arg.Any<CommandFlags>())
            .Returns((RedisValue)$"{GlossaryId}|{TermId}");

        Assert.True(await GlossaryWarpBotStatus.RecordResultAsync(db, "job-1", "FAILED"));

        await db.Received(1).KeyDeleteAsync((RedisKey)GlossaryWarpBotStatus.JobKey("job-1"), Arg.Any<CommandFlags>());
        await db.Received(1).HashIncrementAsync(
            (RedisKey)GlossaryWarpBotStatus.HashKey(GlossaryId), (RedisValue)GlossaryWarpBotStatus.Failed, 1L, Arg.Any<CommandFlags>());
        await db.Received(1).SetAddAsync(
            (RedisKey)GlossaryWarpBotStatus.FailedTermsKey(GlossaryId), (RedisValue)TermId.ToString(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task An_indexed_result_clears_the_term_from_the_failing_set()
    {
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync((RedisKey)GlossaryWarpBotStatus.JobKey("job-2"), Arg.Any<CommandFlags>())
            .Returns((RedisValue)$"{GlossaryId}|{TermId}");

        Assert.True(await GlossaryWarpBotStatus.RecordResultAsync(db, "job-2", "indexed"));

        await db.Received(1).HashIncrementAsync(
            (RedisKey)GlossaryWarpBotStatus.HashKey(GlossaryId), (RedisValue)GlossaryWarpBotStatus.Indexed, 1L, Arg.Any<CommandFlags>());
        await db.Received(1).SetRemoveAsync(
            (RedisKey)GlossaryWarpBotStatus.FailedTermsKey(GlossaryId), (RedisValue)TermId.ToString(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Reading_turns_the_stored_counters_into_a_state()
    {
        var db = Substitute.For<IDatabase>();
        db.HashGetAllAsync((RedisKey)GlossaryWarpBotStatus.HashKey(GlossaryId), Arg.Any<CommandFlags>())
            .Returns(new[]
            {
                new HashEntry("requested", "3"),
                new HashEntry("indexed", "3"),
                new HashEntry("updated_at_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()),
            });
        db.SetLengthAsync((RedisKey)GlossaryWarpBotStatus.FailedTermsKey(GlossaryId), Arg.Any<CommandFlags>()).Returns(0L);

        var status = await GlossaryWarpBotStatus.ReadAsync(db, GlossaryId);

        Assert.Equal("ready", status.State);
        Assert.Equal(3, status.Indexed);
    }
}
