using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Options;
using WarpTalk.BillingService.Infrastructure.Workers;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.Infrastructure.Workers;

public class InvoiceOverdueSweeperTests
{
    [Fact]
    public async Task SweepAsync_Should_Suspend_Subscription_When_Invoice_Is_Past_Grace()
    {
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            InvoiceGraceHours = 1
        };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Plan = plan,
            ServiceState = SubscriptionConstants.ServiceStates.Healthy
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            UserId = subscription.UserId,
            DueAt = DateTime.UtcNow.AddHours(-2),
            Payment = new Payment
            {
                Subscription = subscription
            }
        };

        var invoiceRepository = new Mock<IInvoiceRepository>();
        invoiceRepository
            .Setup(r => r.GetOpenInvoicesDueBeforeAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Invoice>());
        invoiceRepository
            .Setup(r => r.GetOverdueOpenInvoicesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { invoice });

        var subscriptionRepository = new Mock<ISubscriptionRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.InvoiceRepository).Returns(invoiceRepository.Object);
        unitOfWork.Setup(u => u.SubscriptionRepository).Returns(subscriptionRepository.Object);
        unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var aiServiceStateStore = new Mock<IAiServiceStateStore>();
        aiServiceStateStore
            .Setup(s => s.SetAiServiceStateAsync(
                subscription.WorkspaceId,
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(Mock.Of<IDatabase>());

        var services = new ServiceCollection()
            .AddSingleton(unitOfWork.Object)
            .AddSingleton(aiServiceStateStore.Object)
            .AddSingleton(redis.Object)
            .BuildServiceProvider();

        var sweeper = new InvoiceOverdueSweeper(
            services,
            Mock.Of<ILogger<InvoiceOverdueSweeper>>(),
            Options.Create(new BillingWorkerOptions()));

        await sweeper.SweepAsync(CancellationToken.None);

        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Suspended);
        subscription.SuspendedReason.Should().Be(SubscriptionConstants.SuspendedReasons.InvoiceOverdue);
        subscriptionRepository.Verify(r => r.Update(subscription), Times.Once);
        aiServiceStateStore.Verify(s => s.SetAiServiceStateAsync(
            subscription.WorkspaceId,
            SubscriptionConstants.ServiceStates.Suspended,
            SubscriptionConstants.SuspendedReasons.InvoiceOverdue,
            It.IsAny<CancellationToken>()), Times.Once);
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
