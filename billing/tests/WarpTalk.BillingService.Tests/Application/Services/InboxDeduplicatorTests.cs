using FluentAssertions;
using Moq;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Tests.Application.Services;

public sealed class InboxDeduplicatorTests
{
    [Fact]
    public async Task TryAcceptAsync_FirstDelivery_PersistsInboxReceipt()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var inbox = new Mock<IGenericRepository<InboxMessage>>();
        unitOfWork.SetupGet(x => x.InboxMessages).Returns(inbox.Object);
        inbox.Setup(x => x.AnyAsync(It.IsAny<System.Linq.Expressions.Expression<Func<InboxMessage, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var deduplicator = new InboxDeduplicator(unitOfWork.Object);
        var eventId = Guid.NewGuid();

        var accepted = await deduplicator.TryAcceptAsync(eventId, "notification-service", "billing.payment_succeeded");

        accepted.Should().BeTrue();
        inbox.Verify(x => x.AddAsync(It.Is<InboxMessage>(m =>
            m.EventId == eventId
            && m.Consumer == "notification-service"
            && m.EventType == "billing.payment_succeeded"), It.IsAny<CancellationToken>()), Times.Once);
        unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryAcceptAsync_DuplicateDelivery_ReturnsFalseWithoutWriting()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var inbox = new Mock<IGenericRepository<InboxMessage>>();
        unitOfWork.SetupGet(x => x.InboxMessages).Returns(inbox.Object);
        inbox.Setup(x => x.AnyAsync(It.IsAny<System.Linq.Expressions.Expression<Func<InboxMessage, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var deduplicator = new InboxDeduplicator(unitOfWork.Object);

        var accepted = await deduplicator.TryAcceptAsync(Guid.NewGuid(), "notification-service", "billing.payment_succeeded");

        accepted.Should().BeFalse();
        inbox.Verify(x => x.AddAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
