using FluentAssertions;
using Moq;
using System.Linq.Expressions;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Services;

namespace WarpTalk.BillingService.Tests.Infrastructure.Services;

public class BillingCycleClosingServiceTests
{
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

        var usageRecordRepository = new Mock<IGenericRepository<UsageRecord>>();
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

        var service = new BillingCycleClosingService(unitOfWork.Object);

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
        capturedRenewal!.Amount.Should().Be(104_000);
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

        var usageRecordRepository = new Mock<IGenericRepository<UsageRecord>>();
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

        var service = new BillingCycleClosingService(unitOfWork.Object);

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
}
