using FluentAssertions;
using Moq;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Tests.Application.Services;

public sealed class OutboxDispatcherTests
{
    [Fact]
    public async Task DispatchPendingAsync_PublishesMessage_AndMarksItPublished()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventType = "billing.payment_succeeded",
            PayloadJson = "{\"eventId\":\"x\"}",
            AvailableAt = now.AddMinutes(-1),
            CreatedAt = now.AddMinutes(-2)
        };
        var unitOfWork = CreateUnitOfWork(new[] { message });
        var publisher = new Mock<IOutboxEventPublisher>();
        var dispatcher = new OutboxDispatcher(unitOfWork.Object, publisher.Object, new TestTimeProvider(now));

        var dispatched = await dispatcher.DispatchPendingAsync(10);

        dispatched.Should().Be(1);
        message.PublishedAt.Should().Be(now);
        message.AttemptCount.Should().Be(1);
        publisher.Verify(x => x.PublishAsync(message, It.IsAny<CancellationToken>()), Times.Once);
        unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchPendingAsync_WhenPublishFails_SchedulesRetryAndKeepsMessageUnpublished()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventType = "billing.payment_failed",
            PayloadJson = "{}",
            AvailableAt = now.AddMinutes(-1),
            CreatedAt = now.AddMinutes(-2)
        };
        var unitOfWork = CreateUnitOfWork(new[] { message });
        var publisher = new Mock<IOutboxEventPublisher>();
        publisher.Setup(x => x.PublishAsync(message, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker unavailable"));
        var dispatcher = new OutboxDispatcher(unitOfWork.Object, publisher.Object, new TestTimeProvider(now));

        var dispatched = await dispatcher.DispatchPendingAsync(10);

        dispatched.Should().Be(0);
        message.PublishedAt.Should().BeNull();
        message.AttemptCount.Should().Be(1);
        message.AvailableAt.Should().BeOnOrAfter(now.AddSeconds(4));
        message.AvailableAt.Should().BeOnOrBefore(now.AddSeconds(6));
        message.LastError.Should().Be("broker unavailable");
        unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchPendingAsync_DatabaseClaim_DoesNotIncrementAttemptTwice()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventType = "billing.payment_succeeded",
            PayloadJson = "{}",
            AttemptCount = 1,
            AvailableAt = now,
            CreatedAt = now
        };
        var unitOfWork = CreateUnitOfWork(Array.Empty<OutboxMessage>());
        var publisher = new Mock<IOutboxEventPublisher>();
        var claimStore = new Mock<IOutboxClaimStore>();
        claimStore.Setup(x => x.ClaimAsync(10, now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { message });
        var dispatcher = new OutboxDispatcher(
            unitOfWork.Object,
            publisher.Object,
            new TestTimeProvider(now),
            claimStore.Object);

        await dispatcher.DispatchPendingAsync(10);

        message.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task DispatchPendingAsync_TenthFailure_MovesMessageToDeadLetter()
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventType = "billing.payment_failed",
            PayloadJson = "{}",
            AttemptCount = 10,
            AvailableAt = now,
            CreatedAt = now
        };
        var unitOfWork = CreateUnitOfWork(Array.Empty<OutboxMessage>());
        var publisher = new Mock<IOutboxEventPublisher>();
        publisher.Setup(x => x.PublishAsync(message, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("poison message"));
        var claimStore = new Mock<IOutboxClaimStore>();
        claimStore.Setup(x => x.ClaimAsync(10, now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { message });
        var dispatcher = new OutboxDispatcher(
            unitOfWork.Object,
            publisher.Object,
            new TestTimeProvider(now),
            claimStore.Object);

        await dispatcher.DispatchPendingAsync(10);

        message.DeadLetteredAt.Should().Be(now);
        message.PublishedAt.Should().BeNull();
        message.LastError.Should().Be("poison message");
    }

    private static Mock<IUnitOfWork> CreateUnitOfWork(IReadOnlyList<OutboxMessage> messages)
    {
        var repository = new Mock<IGenericRepository<OutboxMessage>>();
        repository.Setup(x => x.GetPagedAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<OutboxMessage, bool>>>(),
                0,
                It.IsAny<int>(),
                It.IsAny<Func<IQueryable<OutboxMessage>, IQueryable<OutboxMessage>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(messages);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(x => x.OutboxMessages).Returns(repository.Object);
        return unitOfWork;
    }

    private sealed class TestTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
