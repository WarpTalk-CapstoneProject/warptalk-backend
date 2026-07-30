using WarpTalk.Gateway.Configuration;

namespace WarpTalk.Gateway.Tests;

public class GatewayRateLimitOptionsTests
{
    [Fact]
    public void Defaults_AreProductionSafe()
    {
        var options = new GatewayRateLimitOptions();

        Assert.Equal(300, options.IpPermitLimit);
        Assert.Equal(180, options.UserPermitLimit);
        Assert.Equal(1_000, options.WorkspacePermitLimit);
        Assert.Equal(5, options.LoginPermitLimit);
        Assert.Equal(60, options.WindowSeconds);
    }

    [Fact]
    public void Validate_RejectsNonPositiveLimits()
    {
        var options = new GatewayRateLimitOptions { IpPermitLimit = 0 };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(nameof(options.IpPermitLimit), exception.Message);
    }
}
