using Moq;
using WarpTalk.NotificationService.Application.Services;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared.Models;

namespace WarpTalk.NotificationService.Tests.Application;

public sealed class RealtimeNotificationPersistenceHandlerTests
{
    [Fact]
    public async Task HandleAsync_NewUserNotification_PersistsItForTheNotificationCenter()
    {
        var notificationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var repository = new Mock<INotificationMessageRepository>();
        repository
            .Setup(x => x.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<NotificationMessage, bool>>>() ))
            .ReturnsAsync(Array.Empty<NotificationMessage>());
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.NotificationMessageRepository).Returns(repository.Object);
        var handler = new RealtimeNotificationPersistenceHandler(unitOfWork.Object);

        var persisted = await handler.HandleAsync(new RealtimeNotificationMessage
        {
            Id = notificationId.ToString(),
            UserId = userId.ToString(),
            Type = "billing.subscription_changed",
            Title = "Subscription updated",
            Content = "Your subscription is active.",
            ActionUrl = "/workspace/payment",
            PayloadJson = "{}",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        Assert.True(persisted);
        repository.Verify(x => x.AddAsync(It.Is<NotificationMessage>(notification =>
            notification.Id == notificationId
            && notification.UserId == userId
            && notification.Type == "billing.subscription_changed")), Times.Once);
        unitOfWork.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_AlreadyPersistedNotification_DoesNotDuplicateIt()
    {
        var notificationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var repository = new Mock<INotificationMessageRepository>();
        repository
            .Setup(x => x.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<NotificationMessage, bool>>>() ))
            .ReturnsAsync(new[] { new NotificationMessage { Id = notificationId, UserId = userId } });
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.NotificationMessageRepository).Returns(repository.Object);
        var handler = new RealtimeNotificationPersistenceHandler(unitOfWork.Object);

        var persisted = await handler.HandleAsync(new RealtimeNotificationMessage
        {
            Id = notificationId.ToString(),
            UserId = userId.ToString(),
            Type = "SYSTEM",
            Title = "Existing",
            Content = "Existing"
        });

        Assert.False(persisted);
        repository.Verify(x => x.AddAsync(It.IsAny<NotificationMessage>()), Times.Never);
        unitOfWork.Verify(x => x.SaveChangesAsync(), Times.Never);
    }

    [Theory]
    [InlineData("", "00000000-0000-0000-0000-000000000001")]
    [InlineData("all", "00000000-0000-0000-0000-000000000001")]
    [InlineData("00000000-0000-0000-0000-000000000001", "not-an-id")]
    public async Task HandleAsync_InvalidIdentity_IsIgnored(string userId, string notificationId)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var handler = new RealtimeNotificationPersistenceHandler(unitOfWork.Object);

        var persisted = await handler.HandleAsync(new RealtimeNotificationMessage
        {
            Id = notificationId,
            UserId = userId,
            Type = "SYSTEM",
            Title = "Invalid target",
            Content = "Invalid target"
        });

        Assert.False(persisted);
        unitOfWork.VerifyNoOtherCalls();
    }
}
