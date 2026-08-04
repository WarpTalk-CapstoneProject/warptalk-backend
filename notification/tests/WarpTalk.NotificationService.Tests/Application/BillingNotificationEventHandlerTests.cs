using System.Text.Json;
using Moq;
using WarpTalk.NotificationService.Application.Services;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared.Events;
using WarpTalk.Shared.Models;

namespace WarpTalk.NotificationService.Tests.Application;

public sealed class BillingNotificationEventHandlerTests
{
    [Fact]
    public async Task HandleAsync_FirstDelivery_WritesNotificationAndInboxInOneSave()
    {
        var eventId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var envelope = new EventEnvelope<BillingPaymentEventPayload>(
            eventId,
            BillingEventTypes.PaymentSucceeded,
            1,
            DateTime.UtcNow,
            "billing-service",
            null,
            null,
            Guid.NewGuid().ToString(),
            new BillingPaymentEventPayload(
                "pi_test", "cs_test", "paid", 299000, "VND", "subscription",
                userId.ToString(), Guid.NewGuid().ToString(), "pro", "monthly", null));
        var message = new OutboxEventMessage
        {
            EventId = eventId,
            EventType = BillingEventTypes.PaymentSucceeded,
            SchemaVersion = 1,
            PayloadJson = JsonSerializer.Serialize(envelope)
        };
        var inboxRepository = new Mock<INotificationInboxMessageRepository>();
        inboxRepository.Setup(x => x.HasProcessedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var notificationRepository = new Mock<INotificationMessageRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.NotificationInboxMessageRepository).Returns(inboxRepository.Object);
        unitOfWork.Setup(x => x.NotificationMessageRepository).Returns(notificationRepository.Object);
        var publisher = new Mock<IMessagePublisher>();
        var handler = new BillingNotificationEventHandler(unitOfWork.Object, publisher.Object);

        var processed = await handler.HandleAsync(message);

        Assert.True(processed);
        inboxRepository.Verify(x => x.AddAsync(It.Is<NotificationInboxMessage>(i =>
            i.EventId == eventId && i.Consumer == BillingNotificationEventHandler.ConsumerName),
            It.IsAny<CancellationToken>()), Times.Once);
        notificationRepository.Verify(x => x.AddAsync(It.Is<NotificationMessage>(n =>
            n.UserId == userId && n.Type == "BILLING_PAYMENT_SUCCEEDED")), Times.Once);
        unitOfWork.Verify(x => x.SaveChangesAsync(), Times.Once);
        publisher.Verify(x => x.PublishAsync(
            "warptalk:notifications:new",
            It.Is<RealtimeNotificationMessage>(notification =>
                notification.UserId == userId.ToString()
                && notification.Type == "BILLING_PAYMENT_SUCCEEDED"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_DuplicateDelivery_DoesNotWriteAgain()
    {
        var eventId = Guid.NewGuid();
        var inboxRepository = new Mock<INotificationInboxMessageRepository>();
        inboxRepository.Setup(x => x.HasProcessedAsync(eventId, BillingNotificationEventHandler.ConsumerName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.NotificationInboxMessageRepository).Returns(inboxRepository.Object);
        var publisher = new Mock<IMessagePublisher>();
        var handler = new BillingNotificationEventHandler(unitOfWork.Object, publisher.Object);

        var processed = await handler.HandleAsync(new OutboxEventMessage
        {
            EventId = eventId,
            EventType = BillingEventTypes.PaymentSucceeded,
            PayloadJson = "{}"
        });

        Assert.False(processed);
        unitOfWork.Verify(x => x.SaveChangesAsync(), Times.Never);
        publisher.VerifyNoOtherCalls();
    }
}
