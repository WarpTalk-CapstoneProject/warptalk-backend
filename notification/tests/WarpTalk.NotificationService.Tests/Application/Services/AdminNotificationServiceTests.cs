using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.NotificationService.Application.DTOs.AdminNotifications;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Application.Services;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.NotificationService.Tests.Application.Services;

public class AdminNotificationServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<IValidator<CreateAdminNotificationDto>> _mockValidator;
    private readonly Mock<IMessagePublisher> _mockPublisher;
    private readonly Mock<ILogger<AdminNotificationService>> _mockLogger;
    private readonly AdminNotificationService _sut;

    public AdminNotificationServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockValidator = new Mock<IValidator<CreateAdminNotificationDto>>();
        _mockPublisher = new Mock<IMessagePublisher>();
        _mockLogger = new Mock<ILogger<AdminNotificationService>>();

        _sut = new AdminNotificationService(_mockUnitOfWork.Object, _mockValidator.Object, _mockPublisher.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task CreateAdminNotificationAsync_ShouldReturnError_WhenValidationFails()
    {
        // Arrange
        var adminId = Guid.NewGuid();
        var dto = new CreateAdminNotificationDto("T", "C", "INVALID", "BROADCAST", null, null);
        
        _mockValidator.Setup(v => v.ValidateAsync(dto, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(new[] { new ValidationFailure("Type", "Invalid type") }));

        // Act
        var result = await _sut.CreateAdminNotificationAsync(adminId, dto);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        _mockUnitOfWork.Verify(u => u.AdminNotificationRepository.AddAsync(It.IsAny<AdminNotification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAdminNotificationAsync_ShouldReturnError_WhenTargetAudienceValidationFails()
    {
        // Arrange
        var adminId = Guid.NewGuid();
        var dto = new CreateAdminNotificationDto(
            "Title", "Content", NotificationConstants.TypeSystem, 
            NotificationConstants.TargetModeSpecificUsers, 
            new List<Guid>(), null); // Empty list, which will fail the Helper
        
        _mockValidator.Setup(v => v.ValidateAsync(dto, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        // We simulate the Helper returning false for empty/too many users. 
        // Wait, the Helper DeduplicateAndValidateUserIds returns (true, empty, null) if empty. 
        // The FluentValidation catches empty. But let's say the Helper catches > 10,000.
        var tooManyUsers = new List<Guid>();
        for (int i = 0; i < 10001; i++) tooManyUsers.Add(Guid.NewGuid());
        
        var badDto = dto with { SpecificUserIds = tooManyUsers };
        
        _mockValidator.Setup(v => v.ValidateAsync(badDto, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult()); // Assume validator passed it, but helper fails

        // Act
        var result = await _sut.CreateAdminNotificationAsync(adminId, badDto);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Contains("exceeds the maximum limit", result.Error);
    }

    [Fact]
    public async Task CreateAdminNotificationAsync_ShouldSucceed_WhenValid()
    {
        // Arrange
        var adminId = Guid.NewGuid();
        var dto = new CreateAdminNotificationDto(
            "Valid Title", "Valid Content", NotificationConstants.TypePromotion, 
            NotificationConstants.TargetModeBroadcast, null, null);
            
        _mockValidator.Setup(v => v.ValidateAsync(dto, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        var mockRepo = new Mock<IAdminNotificationRepository>();
        _mockUnitOfWork.Setup(u => u.AdminNotificationRepository).Returns(mockRepo.Object);

        // Act
        var result = await _sut.CreateAdminNotificationAsync(adminId, dto);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(NotificationConstants.StatusPending, result.Value.Status);
        
        mockRepo.Verify(r => r.AddAsync(It.IsAny<AdminNotification>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
        
        _mockPublisher.Verify(p => p.PublishAsync(
            "admin-notifications-delivery", 
            It.IsAny<DeliveryEventPayload>(), 
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAdminNotificationAsync_WhenSpecificUsers_PublishesChunkedEvents()
    {
        // Arrange
        var adminId = Guid.NewGuid();
        var userIds = new List<Guid>();
        for (int i = 0; i < 2500; i++) userIds.Add(Guid.NewGuid());
        
        var dto = new CreateAdminNotificationDto(
            "Title", "Content", NotificationConstants.TypeSystem, NotificationConstants.TargetModeSpecificUsers, userIds, null);
        
        _mockValidator.Setup(v => v.ValidateAsync(dto, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        var mockRepo = new Mock<IAdminNotificationRepository>();
        _mockUnitOfWork.Setup(u => u.AdminNotificationRepository).Returns(mockRepo.Object);

        // Act
        var result = await _sut.CreateAdminNotificationAsync(adminId, dto);

        // Assert
        Assert.True(result.IsSuccess);
        
        // 2500 users chunked by 1000 = 3 chunks (1000, 1000, 500)
        _mockPublisher.Verify(p => p.PublishAsync(
            "admin-notifications-delivery", 
            It.IsAny<DeliveryEventPayload>(),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
    }
}
