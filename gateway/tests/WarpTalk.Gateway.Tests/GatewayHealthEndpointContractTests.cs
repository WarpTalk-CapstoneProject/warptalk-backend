namespace WarpTalk.Gateway.Tests;

public sealed class GatewayHealthEndpointContractTests
{
    [Fact]
    public void Gateway_ExposesStandardLivenessAndReadinessRoutes()
    {
        var program = File.ReadAllText(FindProgramFile());

        Assert.Contains("MapHealthChecks(\"/health/live\"", program, StringComparison.Ordinal);
        Assert.Contains("MapHealthChecks(\"/health/ready\"", program, StringComparison.Ordinal);
        Assert.Contains(
            "AddWarpTalkRedisReadiness(\"gateway-redis\")",
            program,
            StringComparison.Ordinal);
    }

    private static string FindProgramFile()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "gateway",
                    "src",
                    "WarpTalk.Gateway",
                    "Program.cs");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException("Could not locate Gateway Program.cs.");
    }
}
