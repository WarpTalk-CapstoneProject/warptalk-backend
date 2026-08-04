using FluentAssertions;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Tests.Application.Helpers;

public class BillingIdempotencyKeyHelperTests
{
    [Fact]
    public void ForUsage_Should_Return_Same_Key_For_Same_Payload()
    {
        var request = new RecordUsageRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            UsageConstants.UsageTypes.AiAssistant,
            UsageConstants.UsageUnits.Token,
            100,
            5,
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "details");

        BillingIdempotencyKeyHelper.ForUsage(request)
            .Should()
            .Be(BillingIdempotencyKeyHelper.ForUsage(request));
    }

    [Fact]
    public void ForUsage_Should_Return_Different_Key_For_Different_Payload()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var first = new RecordUsageRequest(workspaceId, userId, "AI", "token", 100, 5, null);
        var second = first with { Quantity = 101 };

        BillingIdempotencyKeyHelper.ForUsage(first)
            .Should()
            .NotBe(BillingIdempotencyKeyHelper.ForUsage(second));
    }

    [Fact]
    public void ForAggregate_Should_Ignore_Source_Key_Order()
    {
        var first = BillingIdempotencyKeyHelper.ForAggregate(new[] { "b", "a" });
        var second = BillingIdempotencyKeyHelper.ForAggregate(new[] { "a", "b" });

        first.Should().Be(second);
    }
}
