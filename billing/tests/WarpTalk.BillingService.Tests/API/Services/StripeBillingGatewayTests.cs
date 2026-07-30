using FluentAssertions;
using Microsoft.Extensions.Configuration;
using WarpTalk.BillingService.API.Services;

namespace WarpTalk.BillingService.Tests.API.Services;

public class StripeBillingGatewayTests
{
    [Fact]
    public async Task GetCheckoutSessionAsync_RejectsMockSessionWithoutCallingStripe()
    {
        var gateway = new StripeBillingGateway(new ConfigurationBuilder().Build());

        var result = await gateway.GetCheckoutSessionAsync("mock_session_payload");

        result.Should().BeNull();
    }

    [Fact]
    public void ParseWebhook_RequiresConfiguredStripeSecrets()
    {
        var gateway = new StripeBillingGateway(new ConfigurationBuilder().Build());

        var act = () => gateway.ParseWebhook("{}", "signature");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Stripe:SecretKey*");
    }
}
