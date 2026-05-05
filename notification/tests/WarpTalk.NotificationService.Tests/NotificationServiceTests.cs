using System;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using WarpTalk.NotificationService.Application.Services;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.NotificationService.Tests;

public class NotificationServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<INotificationMessageRepository> _mockRepo;
    private readonly Application.Services.NotificationService _sut;

    public NotificationServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockRepo = new Mock<INotificationMessageRepository>();

        _mockUnitOfWork.Setup(u => u.NotificationMessageRepository).Returns(_mockRepo.Object);

        var mockLogger = new Mock<Microsoft.Extensions.Logging.ILogger<Application.Services.NotificationService>>();
        _sut = new Application.Services.NotificationService(_mockUnitOfWork.Object, mockLogger.Object);
    }

    [Fact]
    public async Task MarkAsReadAsync_ShouldReturnForbidden_WhenUserIsNotOwner()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var attackerId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();

        var notification = new NotificationMessage
        {
            Id = notificationId,
            UserId = ownerId,
            IsRead = false
        };

        _mockRepo.Setup(r => r.GetByIdAsync(notificationId)).ReturnsAsync(notification);

        // Act
        var result = await _sut.MarkAsReadAsync(attackerId, notificationId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    [Fact]
    public async Task MarkAsReadAsync_ShouldSucceed_WhenUserIsOwner()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();

        var notification = new NotificationMessage
        {
            Id = notificationId,
            UserId = ownerId,
            IsRead = false
        };

        _mockRepo.Setup(r => r.GetByIdAsync(notificationId)).ReturnsAsync(notification);

        // Act
        var result = await _sut.MarkAsReadAsync(ownerId, notificationId);

        // Assert
        Assert.True(result.IsSuccess);
        _mockRepo.Verify(r => r.MarkAsReadAsync(notificationId, ownerId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkAllAsReadAsync_ShouldSucceed()
    {
        // Arrange
        var userId = Guid.NewGuid();

        // Act
        var result = await _sut.MarkAllAsReadAsync(userId);

        // Assert
        Assert.True(result.IsSuccess);
        _mockRepo.Verify(r => r.MarkAllAsReadAsync(userId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
