using FluentAssertions;
using System.Runtime.CompilerServices;

namespace WarpTalk.BillingService.Tests.Architecture;

public class BillingArchitectureTests
{
    [Fact]
    public void Billing_Source_Should_Not_Use_UtcNow_Ticks_For_Idempotency()
    {
        var repoRoot = FindRepoRoot();
        var billingSource = Path.Combine(repoRoot, "billing", "src");
        var offenders = Directory
            .EnumerateFiles(billingSource, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                           !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(file => File.ReadAllText(file).Contains("DateTime.UtcNow.Ticks", StringComparison.Ordinal))
            .ToArray();

        offenders.Should().BeEmpty("billing idempotency keys must be deterministic across retries");
    }

    private static string FindRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        foreach (var startPath in new[] { sourceFilePath, AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = File.Exists(startPath)
                ? new FileInfo(startPath).Directory
                : new DirectoryInfo(startPath);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
                    File.Exists(Path.Combine(directory.FullName, "billing", "WarpTalk.BillingService.slnx")))
                    return directory.FullName;

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
