namespace WarpTalk.Gateway.Tests;

public sealed class BackgroundWorkerLifecycleContractTests
{
    [Fact]
    public void AiResultConsumer_DoesNotLaunchUnobservedTaskRunLoops()
    {
        var source = File.ReadAllText(FindSourceFile(
            "gateway/src/WarpTalk.Gateway/Services/AiResultConsumerService.cs"));

        Assert.DoesNotContain("Task.Run(", source, StringComparison.Ordinal);
        Assert.Contains("Task.WhenAll(", source, StringComparison.Ordinal);
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
