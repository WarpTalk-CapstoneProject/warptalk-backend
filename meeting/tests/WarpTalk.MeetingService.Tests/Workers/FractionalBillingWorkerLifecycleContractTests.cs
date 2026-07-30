namespace WarpTalk.MeetingService.Tests.Workers;

public sealed class SpeechUsageBillingOwnershipContractTests
{
    [Fact]
    public void MeetingService_DoesNotOwnSpeechUsageBilling()
    {
        var backendRoot = FindBackendRoot();
        var programSource = File.ReadAllText(Path.Combine(
            backendRoot,
            "meeting/src/WarpTalk.MeetingService.API/Program.cs"));
        var duplicateWorkerPath = Path.Combine(
            backendRoot,
            "meeting/src/WarpTalk.MeetingService.Infrastructure/Workers/FractionalBillingWorker.cs");

        Assert.DoesNotContain(
            "FractionalBillingWorker",
            programSource,
            StringComparison.Ordinal);
        Assert.False(
            File.Exists(duplicateWorkerPath),
            "Speech usage billing is owned by the AI billing worker; the Meeting service must not run a second stt:results consumer.");
    }

    private static string FindBackendRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "warptalk-backend.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the backend repository root.");
    }
}
