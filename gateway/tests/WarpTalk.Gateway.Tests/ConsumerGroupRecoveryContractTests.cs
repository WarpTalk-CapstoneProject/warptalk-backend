namespace WarpTalk.Gateway.Tests;

/// <summary>
/// Every consume loop must survive its consumer group being deleted underneath it. WT-387.
///
/// WHAT HAPPENED
///   EnsureConsumerGroupWithRetryAsync runs ONCE, before each loop starts. A consumer group lives
///   inside its stream, so deleting the stream deletes the group with it, and every XREADGROUP
///   from then on answers NOGROUP. That exception landed in the loop's generic catch, which logged
///   and slept — forever. The pipeline was dead for the life of the process while every health
///   endpoint went on reporting fine: "live transcript stops mid-meeting and never resumes".
///
///   Two things delete a stream and both are in production. REDIS_STREAM_TTL_SECONDS=3600 — added
///   as the mitigation for this very incident — expires any stream that goes quiet for an hour,
///   which makes this reachable on an ordinary idle night rather than only under memory pressure.
///   And maxmemory-policy allkeys-lru is what deleted live meetings' streams on 2026-08-14.
///
/// WHY THIS IS A SOURCE SCAN
///   The failure mode is a loop that FORGETS the guard, and the thing most likely to forget it is
///   a sixth loop added next year. RedisStreamService is a concrete class over a live
///   IConnectionMultiplexer, so driving a real NOGROUP through five private loops would test the
///   mock rather than the rule. Reading the source asserts the rule directly, in the same shape as
///   BackgroundWorkerLifecycleContractTests above it.
/// </summary>
public sealed class ConsumerGroupRecoveryContractTests
{
    private const string ServicePath = "gateway/src/WarpTalk.Gateway/Services/AiResultConsumerService.cs";

    [Fact]
    public void EveryConsumeLoop_RecoversAVanishedConsumerGroup()
    {
        var source = File.ReadAllText(FindSourceFile(ServicePath));

        var loops = CountOccurrences(source, "private async Task Consume");
        var guards = CountOccurrences(source, "await TryRestoreConsumerGroupAsync(ex, streamKey, ct)) continue;");

        Assert.True(loops > 0, "No consume loops found — this test is pointed at the wrong file.");
        Assert.True(
            guards == loops,
            $"{loops} consume loop(s) but {guards} recover a deleted consumer group. A loop without "
            + "the guard goes deaf permanently the first time its stream is trimmed away, and stays "
            + "deaf until somebody restarts the gateway.");
    }

    /// <summary>
    /// The guard must be the FIRST thing in the catch. Logging an error and sleeping first would
    /// still work, but placing it after the generic handler is how it would quietly stop applying
    /// to whichever loop got reordered.
    /// </summary>
    [Fact]
    public void TheRecoveryRunsBeforeTheGenericErrorHandler()
    {
        var source = File.ReadAllText(FindSourceFile(ServicePath));

        foreach (var (index, _) in IndexesOf(source, "catch (Exception ex)"))
        {
            var block = source[index..Math.Min(index + 400, source.Length)];
            if (!block.Contains("Error consuming", StringComparison.Ordinal)) continue;

            var guardAt = block.IndexOf("TryRestoreConsumerGroupAsync", StringComparison.Ordinal);
            var logAt = block.IndexOf("Error consuming", StringComparison.Ordinal);

            Assert.True(guardAt >= 0, "A consume loop's catch does not attempt recovery at all.");
            Assert.True(guardAt < logAt, "Recovery must be attempted before the failure is logged as unrecoverable.");
        }
    }

    /// <summary>
    /// NOGROUP and nothing else. Recreating a consumer group in response to, say, a connection
    /// failure would turn a transient outage into an unread-message reset on every retry.
    /// </summary>
    [Fact]
    public void OnlyAMissingGroupTriggersRecreation()
    {
        var source = File.ReadAllText(FindSourceFile(ServicePath));

        Assert.Contains("NOGROUP", source, StringComparison.Ordinal);
        Assert.Contains("is not RedisServerException", source, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static IEnumerable<(int Index, string Needle)> IndexesOf(string haystack, string needle)
    {
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            yield return (index, needle);
            index += needle.Length;
        }
    }

    private static string FindSourceFile(string relativePath)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Could not locate {relativePath} from the test working directory.");
    }
}
