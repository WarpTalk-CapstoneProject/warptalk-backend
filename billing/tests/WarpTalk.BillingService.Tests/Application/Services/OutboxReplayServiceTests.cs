using FluentAssertions;
using Moq;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Tests.Application.Services;

public sealed class OutboxReplayServiceTests
{
    [Fact]
    public async Task ReplayAsync_DeadLetteredMessage_MakesItDispatchableAgain()
    {
        var eventId = Guid.NewGuid();
        var message = new OutboxMessage
        {
            Id = eventId,
            DeadLetteredAt = DateTime.UtcNow,
            AttemptCount = 10,
            LastError = "poison message",
            LockedAt = DateTime.UtcNow
        };
        var repository = new Mock<IGenericRepository<OutboxMessage>>();
        repository.Setup(x => x.GetByIdAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(message);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(x => x.OutboxMessages).Returns(repository.Object);
        var replay = new OutboxReplayService(unitOfWork.Object);

        var replayed = await replay.ReplayAsync(eventId);

        replayed.Should().BeTrue();
        message.DeadLetteredAt.Should().BeNull();
        message.LockedAt.Should().BeNull();
        message.LastError.Should().BeNull();
        message.AttemptCount.Should().Be(0);
        repository.Verify(x => x.Update(message), Times.Once);
        unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
