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

    [Fact]
    public void Billing_Application_Should_Not_Depend_On_Api_Infrastructure_Or_Raw_Configuration()
    {
        var repoRoot = FindRepoRoot();
        var applicationSource = Path.Combine(repoRoot, "billing", "src", "WarpTalk.BillingService.Application");
        var forbiddenTokens = new[]
        {
            "WarpTalk.BillingService.API",
            "WarpTalk.BillingService.Infrastructure",
            "Microsoft.Extensions.Configuration",
            "IConfiguration"
        };

        var offenders = Directory
            .EnumerateFiles(applicationSource, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .SelectMany(file => forbiddenTokens
                .Where(token => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(repoRoot, file)} contains {token}"))
            .ToArray();

        offenders.Should().BeEmpty("application services should depend on domain abstractions or typed options, not outer layers or raw configuration");
    }

    [Fact]
    public void Billing_Api_Should_Not_Expose_Demo_Or_Simulation_Endpoints()
    {
        var repoRoot = FindRepoRoot();
        var apiSource = Path.Combine(repoRoot, "billing", "src", "WarpTalk.BillingService.API");
        var forbiddenTokens = new[] { "simulate", "simulation", "demo" };

        var offenders = Directory
            .EnumerateFiles(apiSource, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .SelectMany(file => forbiddenTokens
                .Where(token => File.ReadAllText(file).Contains(token, StringComparison.OrdinalIgnoreCase))
                .Select(token => $"{Path.GetRelativePath(repoRoot, file)} contains {token}"))
            .ToArray();

        offenders.Should().BeEmpty("production API controllers should not expose demo or simulation endpoints");
    }

    [Fact]
    public void Billing_Source_Should_Not_Contain_Mock_Stripe_Checkout_Flow()
    {
        var repoRoot = FindRepoRoot();
        var billingSource = Path.Combine(repoRoot, "billing", "src");
        var forbiddenTokens = new[] { "mock_session_", "mock_pi_", "MockSession", "MockPaymentIntent", "InvalidMockSessionPayload" };

        var offenders = Directory
            .EnumerateFiles(billingSource, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .SelectMany(file => forbiddenTokens
                .Where(token => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(repoRoot, file)} contains {token}"))
            .ToArray();

        offenders.Should().BeEmpty("Stripe checkout should fail when real Stripe configuration is missing instead of creating paid mock sessions");
    }

    private static bool IsBuildOutput(string file)
        => file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
           file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");

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
