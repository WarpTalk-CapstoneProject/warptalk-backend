using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Tests;

public class BillingServiceTests
{
    [Fact]
    public void SubscriptionStatus_ShouldExposeExpectedValues()
    {
        Assert.Equal(1, (int)SubscriptionStatus.Active);
    }
}