using FluentAssertions;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Tests.Application.Mappers;

public class BillingCycleMapperTests
{
    [Fact]
    public void CreateBillingCyclePayment_Should_Map_Internal_Invoice_Payment()
    {
        var subscription = CreateSubscriptionWithPlan();
        var now = new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);

        var payment = PaymentMapper.CreateBillingCyclePayment(new BillingCyclePaymentCreationRequest(
            subscription,
            Subtotal: 100_000m,
            Tax: 10_000m,
            Total: 110_000m,
            OverageCredits: 100,
            Now: now));

        payment.SubscriptionId.Should().Be(subscription.Id);
        payment.UserId.Should().Be(subscription.UserId);
        payment.Provider.Should().Be(PaymentConstants.Providers.InternalInvoice);
        payment.PaymentMethod.Should().Be(PaymentConstants.PaymentMethods.Invoice);
        payment.ProviderTransactionId.Should().Be(BillingCycleTransactionIdHelper.Create(subscription.Id, subscription.CurrentPeriodEnd));
        payment.Status.Should().Be(PaymentConstants.PaymentStatuses.Pending);
        payment.Amount.Should().Be(100_000m);
        payment.TaxAmount.Should().Be(10_000m);
        payment.TotalAmount.Should().Be(110_000m);
        payment.CreatedAt.Should().Be(now);
    }

    [Fact]
    public void CreateBillingCycleInvoice_Should_Map_Open_Invoice_With_Line_Items()
    {
        var subscription = CreateSubscriptionWithPlan();
        var paymentId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);

        var invoice = InvoiceMapper.CreateBillingCycleInvoice(new BillingCycleInvoiceCreationRequest(
            subscription,
            subscription.Plan,
            paymentId,
            ContractPrice: 100_000m,
            OverageCredits: 100,
            OveragePricePerCredit: 4m,
            OverageAmount: 400m,
            UsageBreakdown: new[]
            {
                new BillingCycleUsageBreakdownItem("STT", "second", 120m, 198),
                new BillingCycleUsageBreakdownItem("AI_ASSISTANT", "token_out", 500m, 66)
            },
            Subtotal: 100_400m,
            Tax: 10_040m,
            Total: 110_440m,
            InvoiceTermsDays: 15,
            Now: now));

        invoice.PaymentId.Should().Be(paymentId);
        invoice.Status.Should().Be(InvoiceConstants.InvoiceStatuses.Open);
        invoice.InvoiceNumber.Should().StartWith(InvoiceConstants.Formats.InvoiceNumberPrefix);
        invoice.LineItems.Should().Contain(InvoiceConstants.LineItemTypes.Subscription);
        invoice.LineItems.Should().Contain(InvoiceConstants.LineItemTypes.Overage);
        invoice.LineItems.Should().Contain(InvoiceConstants.LineItemTypes.UsageBreakdown);
        invoice.LineItems.Should().Contain("STT");
        invoice.LineItems.Should().Contain("AI_ASSISTANT");
        invoice.LineItems.Should().Contain(InvoiceConstants.LineItemDescriptions.UsageOverCommittedCredits);
        invoice.DueAt.Should().Be(now.AddDays(15));
    }

    [Fact]
    public void BillingCycleMappers_Should_Require_Loaded_Subscription_Plan()
    {
        var subscription = CreateSubscriptionWithPlan();
        subscription.Plan = null!;

        var paymentAction = () => PaymentMapper.CreateBillingCyclePayment(new BillingCyclePaymentCreationRequest(
            subscription,
            100m,
            10m,
            110m,
            0,
            DateTime.UtcNow));

        var invoiceAction = () => InvoiceMapper.CreateBillingCycleInvoice(new BillingCycleInvoiceCreationRequest(
            subscription,
            new Plan(),
            Guid.NewGuid(),
            100m,
            0,
            4m,
            0m,
            Array.Empty<BillingCycleUsageBreakdownItem>(),
            100m,
            10m,
            110m,
            15,
            DateTime.UtcNow));

        paymentAction.Should().Throw<ArgumentException>();
        invoiceAction.Should().Throw<ArgumentException>();
    }

    private static Subscription CreateSubscriptionWithPlan()
    {
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "enterprise",
            Currency = PaymentConstants.Currencies.VndAccounting,
            BillingCycle = SubscriptionConstants.BillingCycles.Monthly,
            CreditsPerCycle = 20_000,
            OveragePricePerCredit = 4m
        };

        return new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            PlanId = plan.Id,
            Plan = plan,
            CurrentPeriodStart = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            CurrentPeriodEnd = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }
}
