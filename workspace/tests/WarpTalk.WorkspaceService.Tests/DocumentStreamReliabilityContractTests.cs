using System;
using System.IO;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// WT-428. Both document stream consumers must reclaim entries nobody acknowledged.
///
/// Seven .NET consumers in this codebase already do — and a sibling contract test in
/// MeetingService pins two of them. These two were the exception, and it showed: production
/// reached 678 unacknowledged entries on `workspace-document-embedding-results`, flat, with lag
/// 0. Nothing was undelivered; everything had been handed out and abandoned.
///
/// The abandonment is structural, not incidental. The consumer name carries a fresh GUID per
/// process, so entries in flight at a restart are addressed to a name that will never exist
/// again, and `"&gt;"` returns only never-delivered entries — so without a reclaim there is no
/// path back to them. A single throw stranded its whole batch the same way.
///
/// A source-level assertion rather than a behavioural one because the failure is an ABSENCE:
/// no test that drives a working consumer can notice a recovery path that was never written.
/// </summary>
public sealed class DocumentStreamReliabilityContractTests
{
    private const string EmbeddingResultConsumer =
        "workspace/src/WarpTalk.WorkspaceService.Infrastructure/BackgroundServices/DocumentEmbeddingIndexResultConsumerService.cs";

    private const string GuardrailConsumer =
        "workspace/src/WarpTalk.WorkspaceService.Infrastructure/BackgroundServices/DocumentSecurityGuardrailConsumerService.cs";

    [Theory]
    [InlineData(EmbeddingResultConsumer)]
    [InlineData(GuardrailConsumer)]
    public void DocumentConsumers_ReclaimEntriesNobodyAcknowledged(string relativePath)
    {
        var source = File.ReadAllText(FindSourceFile(relativePath));

        Assert.Contains("StreamAutoClaimAsync(", source, StringComparison.Ordinal);
        Assert.Contains("ReclaimIdleMilliseconds", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddingResultConsumer_IsolatesAndDeadLettersAPoisonEntry()
    {
        // The batch loop aborted on the first throw, leaving every entry in that batch pending
        // — which is how a handful of bad results became 678. Per-entry handling plus a bounded
        // retry is what stops one message taking the other nine with it.
        var source = File.ReadAllText(FindSourceFile(EmbeddingResultConsumer));

        Assert.Contains("HashIncrementAsync(", source, StringComparison.Ordinal);
        Assert.Contains("MaxAttempts", source, StringComparison.Ordinal);
        Assert.Contains("MoveToDeadLetterAsync(", source, StringComparison.Ordinal);
        Assert.Contains("DeadLetterStreamName", source, StringComparison.Ordinal);
        Assert.Contains("StreamAcknowledgeAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryWriterOfFailedIngestionStatusAlsoRecordsAReason()
    {
        // WT-411 introduced ingestion_failure_reason so an outage could be told from a policy
        // refusal, and covered the guardrail's branches. The embedding-result path — the only
        // other writer of 'failed' — was missed, and wrote NULL for six production documents.
        var processor = File.ReadAllText(FindSourceFile(
            "workspace/src/WarpTalk.WorkspaceService.Infrastructure/Adapters/DocumentEmbeddingResultProcessor.cs"));

        Assert.Contains("IngestionFailureReason", processor, StringComparison.Ordinal);
        Assert.Contains(
            "WorkspaceDocumentIngestionFailureReasons.EmbeddingFailed",
            processor,
            StringComparison.Ordinal);
        Assert.Contains(
            "WorkspaceDocumentIngestionFailureReasons.EmbeddingBlocked",
            processor,
            StringComparison.Ordinal);
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

        throw new FileNotFoundException($"Could not locate {relativePath}.");
    }
}
