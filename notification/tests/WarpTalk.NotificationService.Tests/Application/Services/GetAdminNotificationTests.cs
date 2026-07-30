using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using FluentValidation;
using WarpTalk.NotificationService.Application.DTOs.AdminNotifications;
using WarpTalk.NotificationService.Application.Services;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.NotificationService.Tests.Application.Services;

public class GetAdminNotificationTests
{
    private readonly Mock<IUnitOfWork> _mockUoW;
    private readonly Mock<IAdminNotificationRepository> _mockRepo;
    private readonly Mock<ILogger<AdminNotificationService>> _mockLogger;
    private readonly AdminNotificationService _service;

    public GetAdminNotificationTests()
    {
        _mockUoW = new Mock<IUnitOfWork>();
        _mockRepo = new Mock<IAdminNotificationRepository>();
        _mockUoW.Setup(u => u.AdminNotificationRepository).Returns(_mockRepo.Object);

        var mockValidator = new Mock<IValidator<CreateAdminNotificationDto>>();
        var mockPublisher = new Mock<IMessagePublisher>();
        _mockLogger = new Mock<ILogger<AdminNotificationService>>();

        _service = new AdminNotificationService(_mockUoW.Object, mockValidator.Object, mockPublisher.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task GetAdminNotificationDetailAsync_WhenExists_ReturnsDetailDto()
    {
        // Arrange
        var id = Guid.NewGuid();
        var entity = new AdminNotification
        {
            Id = id,
            Title = "Test",
            Content = "Content",
            Type = NotificationConstants.TypeSystem,
            Status = NotificationConstants.StatusSent,
            TargetAudienceMode = NotificationConstants.TargetModeBroadcast,
            CreatedBy = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _mockRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        // Act
        var result = await _service.GetAdminNotificationDetailAsync(id);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(id, result.Value.Id);
        Assert.Equal("Test", result.Value.Title);
    }

    [Fact]
    public async Task GetAdminNotificationDetailAsync_WhenNotFound_ReturnsNotFound()
    {
        // Arrange
        var id = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AdminNotification?)null);

        // Act
        var result = await _service.GetAdminNotificationDetailAsync(id);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    // Since GetAdminNotificationsAsync involves complex filtering that we will mock to pass in this layer test,
    // we just want to test that the service calls the repo and maps properly.
    [Fact]
    public async Task GetAdminNotificationsAsync_ReturnsPaginatedResponse()
    {
        // Arrange
        var query = new GetAdminNotificationsQuery(1, 10, "Test", null, null);
        var items = new List<AdminNotification>
        {
            new AdminNotification { Id = Guid.NewGuid(), Title = "Test 1" },
            new AdminNotification { Id = Guid.NewGuid(), Title = "Test 2" }
        };

        // We will need to implement a way to mock the paginated search on the repo.
        _mockRepo.Setup(r => r.GetPaginatedAsync(
            It.IsAny<WarpTalk.NotificationService.Domain.Models.AdminNotificationFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((items, 2));

        // Act
        var result = await _service.GetAdminNotificationsAsync(query);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value.TotalCount);
        Assert.Contains(result.Value.Items, i => i.Title == "Test 1");
    }
}
