using System;
using System.IO;
using System.Linq;
using Xunit;

namespace WarpTalk.MeetingService.Tests.Services;

/// <summary>
/// Every BackgroundService this project defines must actually be registered.
///
/// MeetingChatAssistantResultConsumerService was not. The class had a consumer group, a retry
/// policy, a dead-letter stream and a guarded XGROUP creation — 250 lines of correct code that
/// nothing ever started. So every @WarpBot mention was published to the AI worker, answered by
/// it, and then dropped: on production the meeting-chat-consumers group sat 41 entries behind
/// with ZERO pending. Not stuck. Never read.
///
/// It cost two days of investigation, because a consumer that was never started is invisible
/// in exactly the same way as one that is working and has nothing to do.
///
/// A source-text check rather than a DI probe on purpose: building the real container needs a
/// database, Redis and a gateway, so the cheap check is the one that will actually be kept.
/// </summary>
public class HostedServiceRegistrationTests
{
    private static string RepositoryFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return Path.Combine(new[] { directory!.FullName }.Concat(relativeParts).ToArray());
    }

    [Fact]
    public void EveryBackgroundServiceIsRegistered()
    {
        var program = File.ReadAllText(RepositoryFile("src", "WarpTalk.MeetingService.API", "Program.cs"));
        var apiRoot = Path.GetDirectoryName(RepositoryFile("src", "WarpTalk.MeetingService.API", "Program.cs"))!;

        var backgroundServices = Directory
            .EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains(": BackgroundService"))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .ToList();

        Assert.NotEmpty(backgroundServices);

        var unregistered = backgroundServices
            .Where(name => !program.Contains($"AddHostedService<{name}>")
                        && !program.Contains($".{name}>"))
            .ToList();

        Assert.True(
            unregistered.Count == 0,
            $"Defined but never started: {string.Join(", ", unregistered)}. "
            + "A hosted service nobody registers is indistinguishable from one that works and "
            + "has nothing to do.");
    }
}
