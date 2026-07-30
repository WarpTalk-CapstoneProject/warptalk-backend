using System.Text.Json;
using Moq;
using WarpTalk.NotificationService.Application.Services;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared.Events;

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
        var inboxRepository = new Mock<IGenericRepository<NotificationInboxMessage>>();
        inboxRepository.Setup(x => x.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<NotificationInboxMessage, bool>>>()))
            .ReturnsAsync(Array.Empty<NotificationInboxMessage>());
        var notificationRepository = new Mock<IGenericRepository<NotificationMessage>>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.Repository<NotificationInboxMessage>()).Returns(inboxRepository.Object);
        unitOfWork.Setup(x => x.Repository<NotificationMessage>()).Returns(notificationRepository.Object);
        var handler = new BillingNotificationEventHandler(unitOfWork.Object);

        var processed = await handler.HandleAsync(message);

        Assert.True(processed);
        inboxRepository.Verify(x => x.AddAsync(It.Is<NotificationInboxMessage>(i =>
            i.EventId == eventId && i.Consumer == BillingNotificationEventHandler.ConsumerName)), Times.Once);
        notificationRepository.Verify(x => x.AddAsync(It.Is<NotificationMessage>(n =>
            n.UserId == userId && n.Type == "BILLING_PAYMENT_SUCCEEDED")), Times.Once);
        unitOfWork.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_DuplicateDelivery_DoesNotWriteAgain()
    {
        var eventId = Guid.NewGuid();
        var inboxRepository = new Mock<IGenericRepository<NotificationInboxMessage>>();
        inboxRepository.Setup(x => x.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<NotificationInboxMessage, bool>>>()))
            .ReturnsAsync(new[]
            {
                new NotificationInboxMessage { EventId = eventId, Consumer = BillingNotificationEventHandler.ConsumerName }
            });
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.Repository<NotificationInboxMessage>()).Returns(inboxRepository.Object);
        var handler = new BillingNotificationEventHandler(unitOfWork.Object);

        var processed = await handler.HandleAsync(new OutboxEventMessage
        {
            EventId = eventId,
            EventType = BillingEventTypes.PaymentSucceeded,
            PayloadJson = "{}"
        });

        Assert.False(processed);
        unitOfWork.Verify(x => x.SaveChangesAsync(), Times.Never);
    }
}
