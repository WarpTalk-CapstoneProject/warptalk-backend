using WarpTalk.BillingService.Domain.Constants;
using Xunit;

namespace WarpTalk.BillingService.Tests;

public class BillingServiceTests
{
    [Fact]
    public void SubscriptionStatus_ShouldExposeExpectedValues()
    {
        Assert.Equal("active", BillingConstants.SubscriptionStatuses.Active);
    }
}