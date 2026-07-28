namespace WarpTalk.MeetingService.Tests.Workers;

public sealed class AssistantChatStreamReliabilityContractTests
{
    [Theory]
    [InlineData("meeting/src/WarpTalk.MeetingService.API/HostedServices/MeetingChatAssistantResultConsumerService.cs")]
    [InlineData("assistant/src/WarpTalk.AssistantService.API/Services/AssistantChatResultConsumerService.cs")]
    public void AssistantResultConsumers_ReclaimRetryAndDeadLetterBeforeAcknowledgingPoisonMessages(
        string relativePath)
    {
        var source = File.ReadAllText(FindSourceFile(relativePath));

        Assert.Contains("StreamAutoClaimAsync(", source, StringComparison.Ordinal);
        Assert.Contains("HashIncrementAsync(", source, StringComparison.Ordinal);
        Assert.Contains("MoveToDeadLetterAsync(", source, StringComparison.Ordinal);
        Assert.Contains("DeadLetterStreamName", source, StringComparison.Ordinal);
        Assert.Contains("StreamAcknowledgeAsync(", source, StringComparison.Ordinal);
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
