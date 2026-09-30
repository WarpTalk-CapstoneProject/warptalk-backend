using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Services;
using WarpTalk.BillingService.Infrastructure.Services;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.Infrastructure.Services;

public class BillingCycleClosingServiceTests
{
    private static IBillingPolicyService BillingPolicyService
    {
        get
        {
            var billingPolicyService = new Mock<IBillingPolicyService>();
            billingPolicyService
                .Setup(s => s.GetPolicyAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new BillingPolicyDto(0.10m));
            return billingPolicyService.Object;
        }
    }

    [Fact]
    public async Task CloseDueCyclesAsync_Should_Create_Payment_Invoice_And_Renewal_When_Subscription_Has_Overage()
    {
        var now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Enterprise",
            Price = 1_000_000m,
            CreditsPerCycle = 100_000,
            RolloverCapCredits = 10_000,
            OveragePricePerCredit = 5m,
            InvoiceTermsDays = 15,
            BillingCycle = SubscriptionConstants.BillingCycles.Monthly
        };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            PlanId = plan.Id,
            Plan = plan,
            CurrentPeriodStart = now.AddMonths(-1),
            CurrentPeriodEnd = now,
            CreditsRemaining = 4_000,
            CreditsUsedThisCycle = 130_000,
            OverageCreditsThisCycle = 30_000,
            OverageStartedAt = now.AddDays(-3),
            ServiceState = SubscriptionConstants.ServiceStates.InOverage,
            SuspendedReason = "overage"
        };

        Payment? capturedPayment = null;
        Invoice? capturedInvoice = null;
        CreditTransaction? capturedRenewal = null;

        var subscriptionRepository = new Mock<ISubscriptionRepository>();
        subscriptionRepository
            .Setup(r => r.GetDueForRenewalAsync(now, now.Subtract(TimeSpan.FromDays(2)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { subscription });

        var paymentRepository = new Mock<IPaymentRepository>();
        paymentRepository
            .Setup(r => r.AddAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()))
            .Callback<Payment, CancellationToken>((payment, _) => capturedPayment = payment)
            .Returns(Task.CompletedTask);

        var invoiceRepository = new Mock<IInvoiceRepository>();
        invoiceRepository
            .Setup(r => r.AddAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()))
            .Callback<Invoice, CancellationToken>((invoice, _) => capturedInvoice = invoice)
            .Returns(Task.CompletedTask);

        var creditTransactionRepository = new Mock<ICreditTransactionRepository>();
        creditTransactionRepository
            .Setup(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()))
            .Callback<CreditTransaction, CancellationToken>((transaction, _) => capturedRenewal = transaction)
            .Returns(Task.CompletedTask);

        var usageRecordRepository = new Mock<IUsageRecordRepository>();
        usageRecordRepository
            .Setup(r => r.FindAsync(
                It.IsAny<Expression<Func<UsageRecord, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new UsageRecord
                {
                    SubscriptionId = subscription.Id,
                    UsageType = "STT",
                    Unit = "second",
                    Quantity = 120m,
                    CreditsConsumed = 198,
                    RecordedAt = now.AddDays(-2)
                },
                new UsageRecord
                {
                    SubscriptionId = subscription.Id,
                    UsageType = "TRANSLATION",
                    Unit = "token_out",
                    Quantity = 1_000m,
                    CreditsConsumed = 27,
                    RecordedAt = now.AddDays(-1)
                }
            });

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.SubscriptionRepository).Returns(subscriptionRepository.Object);
        unitOfWork.Setup(u => u.PaymentRepository).Returns(paymentRepository.Object);
        unitOfWork.Setup(u => u.InvoiceRepository).Returns(invoiceRepository.Object);
        unitOfWork.Setup(u => u.CreditTransactionRepository).Returns(creditTransactionRepository.Object);
        unitOfWork.Setup(u => u.UsageRecordRepository).Returns(usageRecordRepository.Object);
        unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var domainService = new SubscriptionDomainService();
        var service = new BillingCycleClosingService(unitOfWork.Object, domainService, BillingPolicyService, NullLogger<BillingCycleClosingService>.Instance);

        var result = await service.CloseDueCyclesAsync(now, TimeSpan.FromDays(2), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);
        capturedPayment.Should().NotBeNull();
        capturedInvoice.Should().NotBeNull();
        capturedRenewal.Should().NotBeNull();
        capturedPayment!.Amount.Should().Be(1_150_000m);
        capturedPayment.TaxAmount.Should().Be(115_000m);
        capturedPayment.TotalAmount.Should().Be(1_265_000m);
        capturedInvoice!.Subtotal.Should().Be(1_150_000m);
        capturedInvoice.Total.Should().Be(1_265_000m);
        capturedInvoice.DueAt.Should().Be(now.AddDays(15));
        capturedInvoice.LineItems.Should().Contain(InvoiceConstants.LineItemTypes.UsageBreakdown);
        capturedInvoice.LineItems.Should().Contain("STT");
        capturedInvoice.LineItems.Should().Contain("TRANSLATION");
        capturedRenewal!.Amount.Should().Be(100_000);
        capturedRenewal.BalanceAfter.Should().Be(104_000);
        capturedRenewal.ReferenceId.Should().Be(capturedInvoice.Id);
        subscription.CreditsRemaining.Should().Be(104_000);
        subscription.CreditsUsedThisCycle.Should().Be(0);
        subscription.OverageCreditsThisCycle.Should().Be(0);
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
        subscription.SuspendedReason.Should().BeNull();
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CloseWorkspaceCycleAsync_Should_Close_Target_Workspace_Immediately()
    {
        var now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
        var workspaceId = Guid.NewGuid();
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Enterprise",
            Price = 1_900_000m,
            CreditsPerCycle = 700_000,
            RolloverCapCredits = 700_000,
            OveragePricePerCredit = 4m,
            InvoiceTermsDays = 15,
            BillingCycle = SubscriptionConstants.BillingCycles.Monthly
        };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            PlanId = plan.Id,
            Plan = plan,
            Status = SubscriptionConstants.SubscriptionStatuses.Active,
            IsActive = true,
            AutoRenew = true,
            CurrentPeriodStart = now.AddDays(-1),
            CurrentPeriodEnd = now.AddDays(30),
            CreditsRemaining = 710_000,
            CreditsUsedThisCycle = 0
        };

        Invoice? capturedInvoice = null;

        var subscriptionRepository = new Mock<ISubscriptionRepository>();
        subscriptionRepository
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Subscription, bool>>>(),
                "Plan",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(subscription);

        var paymentRepository = new Mock<IPaymentRepository>();
        paymentRepository
            .Setup(r => r.AddAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var invoiceRepository = new Mock<IInvoiceRepository>();
        invoiceRepository
            .Setup(r => r.AddAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()))
            .Callback<Invoice, CancellationToken>((invoice, _) => capturedInvoice = invoice)
            .Returns(Task.CompletedTask);

        var creditTransactionRepository = new Mock<ICreditTransactionRepository>();
        creditTransactionRepository
            .Setup(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var usageRecordRepository = new Mock<IUsageRecordRepository>();
        usageRecordRepository
            .Setup(r => r.FindAsync(
                It.IsAny<Expression<Func<UsageRecord, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UsageRecord>());

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.SubscriptionRepository).Returns(subscriptionRepository.Object);
        unitOfWork.Setup(u => u.PaymentRepository).Returns(paymentRepository.Object);
        unitOfWork.Setup(u => u.InvoiceRepository).Returns(invoiceRepository.Object);
        unitOfWork.Setup(u => u.CreditTransactionRepository).Returns(creditTransactionRepository.Object);
        unitOfWork.Setup(u => u.UsageRecordRepository).Returns(usageRecordRepository.Object);
        unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var domainService = new SubscriptionDomainService();
        var service = new BillingCycleClosingService(unitOfWork.Object, domainService, BillingPolicyService, NullLogger<BillingCycleClosingService>.Instance);

        var result = await service.CloseWorkspaceCycleAsync(workspaceId, now, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);
        capturedInvoice.Should().NotBeNull();
        capturedInvoice!.Subtotal.Should().Be(1_900_000m);
        capturedInvoice.Total.Should().Be(2_090_000m);
        subscription.CurrentPeriodStart.Should().Be(now.AddMinutes(-1));
        subscription.CurrentPeriodEnd.Should().Be(now.AddMinutes(-1).AddMonths(1));
        subscription.CreditsRemaining.Should().Be(1_400_000);
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Currency follows the amount source ──────────────────────────────────────────────────

    [Fact]
    public async Task CloseDueCyclesAsync_Should_Invoice_Contract_Price_In_Vnd_On_A_Usd_Plan()
    {
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var plan = UsdPlan();
        var subscription = DueSubscription(plan, now);
        subscription.ContractPriceVnd = 1_900_000m;
        subscription.OveragePricePerCreditOverride = 4m;
        subscription.OverageCreditsThisCycle = 1_000;

        var harness = new Harness(subscription, now);
        var result = await harness.Service.CloseDueCyclesAsync(now, TimeSpan.FromDays(2), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);
        harness.Invoice!.Currency.Should().Be(PaymentConstants.Currencies.VndAccounting);
        harness.Payment!.Currency.Should().Be(PaymentConstants.Currencies.VndAccounting);
        harness.Invoice.Subtotal.Should().Be(1_904_000m);
        harness.Invoice.Total.Should().Be(2_094_400m);
        harness.Payment.TotalAmount.Should().Be(2_094_400m);
    }

    [Fact]
    public async Task CloseDueCyclesAsync_Should_Keep_Plan_Currency_Without_Contract_Terms()
    {
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var plan = UsdPlan();
        var subscription = DueSubscription(plan, now);
        subscription.OverageCreditsThisCycle = 100;

        var harness = new Harness(subscription, now);
        await harness.Service.CloseDueCyclesAsync(now, TimeSpan.FromDays(2), CancellationToken.None);

        harness.Invoice!.Currency.Should().Be("usd");
        harness.Payment!.Currency.Should().Be("usd");
        harness.Invoice.Subtotal.Should().Be(49m + 100 * 0.02m);
    }

    [Fact]
    public async Task CloseDueCyclesAsync_Should_Refuse_A_Cycle_That_Would_Add_Vnd_To_Usd_And_Still_Close_The_Rest()
    {
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var usdPlan = UsdPlan();
        var mixed = DueSubscription(usdPlan, now);
        mixed.ContractPriceVnd = 1_900_000m;        // VND contract price
        mixed.OverageCreditsThisCycle = 500;        // ...but the overage rate is the plan's USD one
        var mixedPeriodEnd = mixed.CurrentPeriodEnd;
        var healthy = DueSubscription(UsdPlan(), now);

        var harness = new Harness(new[] { mixed, healthy }, now);
        var result = await harness.Service.CloseDueCyclesAsync(now, TimeSpan.FromDays(2), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1, "only the subscription with one currency is closed");
        harness.Invoices.Should().ContainSingle().Which.Currency.Should().Be("usd");
        harness.Payments.Should().ContainSingle().Which.SubscriptionId.Should().Be(healthy.Id);
        mixed.CurrentPeriodEnd.Should().Be(mixedPeriodEnd, "a refused cycle stays due and untouched");
        mixed.OverageCreditsThisCycle.Should().Be(500);
    }

    [Fact]
    public async Task CloseDueCyclesAsync_Should_Not_Treat_An_Unused_Usd_Overage_Rate_As_A_Second_Currency()
    {
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var subscription = DueSubscription(UsdPlan(), now);
        subscription.ContractPriceVnd = 1_900_000m;
        subscription.OverageCreditsThisCycle = 0;

        var harness = new Harness(subscription, now);
        var result = await harness.Service.CloseDueCyclesAsync(now, TimeSpan.FromDays(2), CancellationToken.None);

        result.Value.Should().Be(1);
        harness.Invoice!.Currency.Should().Be(PaymentConstants.Currencies.VndAccounting);
        harness.Invoice.Subtotal.Should().Be(1_900_000m);
    }

    [Fact]
    public async Task CloseWorkspaceCycleAsync_Should_Fail_With_Conflict_On_Mixed_Currencies_And_Restore_The_Period()
    {
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var subscription = DueSubscription(UsdPlan(), now);
        subscription.CurrentPeriodEnd = now.AddDays(20);
        subscription.OveragePricePerCreditOverride = 4m;  // VND overage rate on a USD base price
        subscription.OverageCreditsThisCycle = 10;

        var harness = new Harness(subscription, now);
        var result = await harness.Service.CloseWorkspaceCycleAsync(subscription.WorkspaceId, now, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionConflict);
        subscription.CurrentPeriodEnd.Should().Be(now.AddDays(20));
        harness.Invoices.Should().BeEmpty();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloseDueCyclesAsync_Should_Write_Invariant_Line_Item_Amounts_Under_A_Comma_Decimal_Culture()
    {
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var subscription = DueSubscription(UsdPlan(), now);
        subscription.OverageCreditsThisCycle = 3;

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("vi-VN");
            var harness = new Harness(subscription, now);
            await harness.Service.CloseDueCyclesAsync(now, TimeSpan.FromDays(2), CancellationToken.None);

            using var lineItems = JsonDocument.Parse(harness.Invoice!.LineItems);
            var overage = lineItems.RootElement.EnumerateArray()
                .Single(item => item.GetProperty("type").GetString() == InvoiceConstants.LineItemTypes.Overage);
            overage.GetProperty("unitPrice").GetRawText().Should().Be("0.02");
            overage.GetProperty("amount").GetRawText().Should().Be("0.06");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static Plan UsdPlan() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Business (USD)",
        Price = 49m,
        Currency = "usd",
        CreditsPerCycle = 10_000,
        RolloverCapCredits = 0,
        OveragePricePerCredit = 0.02m,
        InvoiceTermsDays = 15,
        BillingCycle = SubscriptionConstants.BillingCycles.Monthly
    };

    private static Subscription DueSubscription(Plan plan, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        PlanId = plan.Id,
        Plan = plan,
        Status = SubscriptionConstants.SubscriptionStatuses.Active,
        IsActive = true,
        AutoRenew = true,
        CurrentPeriodStart = now.AddMonths(-1),
        CurrentPeriodEnd = now,
        CreditsRemaining = 0
    };

    private sealed class Harness
    {
        public List<Payment> Payments { get; } = new();
        public List<Invoice> Invoices { get; } = new();
        public Payment? Payment => Payments.SingleOrDefault();
        public Invoice? Invoice => Invoices.SingleOrDefault();
        public Mock<IUnitOfWork> UnitOfWork { get; } = new();
        public BillingCycleClosingService Service { get; }

        public Harness(Subscription subscription, DateTime now) : this(new[] { subscription }, now) { }

        public Harness(IReadOnlyList<Subscription> subscriptions, DateTime now)
        {
            var subscriptionRepository = new Mock<ISubscriptionRepository>();
            subscriptionRepository
                .Setup(r => r.GetDueForRenewalAsync(now, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(subscriptions);
            subscriptionRepository
                .Setup(r => r.FirstOrDefaultAsync(
                    It.IsAny<Expression<Func<Subscription, bool>>>(),
                    "Plan",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(subscriptions[0]);

            var paymentRepository = new Mock<IPaymentRepository>();
            paymentRepository
                .Setup(r => r.AddAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()))
                .Callback<Payment, CancellationToken>((payment, _) => Payments.Add(payment))
                .Returns(Task.CompletedTask);

            var invoiceRepository = new Mock<IInvoiceRepository>();
            invoiceRepository
                .Setup(r => r.AddAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()))
                .Callback<Invoice, CancellationToken>((invoice, _) => Invoices.Add(invoice))
                .Returns(Task.CompletedTask);

            var creditTransactionRepository = new Mock<ICreditTransactionRepository>();
            creditTransactionRepository
                .Setup(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var usageRecordRepository = new Mock<IUsageRecordRepository>();
            usageRecordRepository
                .Setup(r => r.FindAsync(
                    It.IsAny<Expression<Func<UsageRecord, bool>>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<UsageRecord>());

            UnitOfWork.Setup(u => u.SubscriptionRepository).Returns(subscriptionRepository.Object);
            UnitOfWork.Setup(u => u.PaymentRepository).Returns(paymentRepository.Object);
            UnitOfWork.Setup(u => u.InvoiceRepository).Returns(invoiceRepository.Object);
            UnitOfWork.Setup(u => u.CreditTransactionRepository).Returns(creditTransactionRepository.Object);
            UnitOfWork.Setup(u => u.UsageRecordRepository).Returns(usageRecordRepository.Object);
            UnitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            Service = new BillingCycleClosingService(
                UnitOfWork.Object,
                new SubscriptionDomainService(),
                BillingPolicyService,
                NullLogger<BillingCycleClosingService>.Instance);
        }
    }
}
