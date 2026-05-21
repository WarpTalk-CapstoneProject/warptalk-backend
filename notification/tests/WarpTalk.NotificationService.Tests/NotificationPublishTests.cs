using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using WarpTalk.NotificationService.Application.DTOs;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.Shared;
using WarpTalk.Shared.Models;
using Xunit;

namespace WarpTalk.NotificationService.Tests;

public class NotificationPublishTests
{
    [Fact]
    public async Task SeedMockNotification_ShouldCreateAndPublishToRedis()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        
        var mockNotificationService = new Mock<INotificationService>();
        var mockRedis = new Mock<IConnectionMultiplexer>();
        var mockDb = new Mock<IDatabase>();
        var mockLogger = new Mock<ILogger>();

        var dto = new NotificationMessageDto(
            notificationId, 
            NotificationConstants.TypeSystemAlert, 
            "Mock Notification", 
            "This is a seeded notification for testing.", 
            null, 
            "{}", 
            false, 
            null, 
            DateTime.UtcNow);

        mockNotificationService
            .Setup(s => s.CreateNotificationAsync(It.IsAny<CreateNotificationMessageDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(dto));

        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDb.Object);

        // Act - Simulating the old controller SeedMockNotification logic
        var createDto = new CreateNotificationMessageDto(
            userId, 
            NotificationConstants.TypeSystemAlert, 
            "Mock Notification", 
            "This is a seeded notification for testing.", 
            null, 
            "{}"
        );
        var result = await mockNotificationService.Object.CreateNotificationAsync(createDto, CancellationToken.None);

        Assert.True(result.IsSuccess);

        var msg = new RealtimeNotificationMessage
        {
            Id = result.Value.Id.ToString(),
            UserId = userId.ToString(),
            Type = result.Value.Type,
            Title = result.Value.Title,
            Content = result.Value.Content,
            ActionUrl = result.Value.ActionUrl ?? string.Empty,
            PayloadJson = result.Value.PayloadJson,
            CreatedAt = result.Value.CreatedAt.ToString("O")
        };
        
        var json = JsonSerializer.Serialize(msg);
        
        await mockRedis.Object.GetDatabase().PublishAsync(RedisChannel.Literal(NotificationConstants.RedisNewNotificationChannel), json);

        // Assert
        mockDb.Verify(d => d.PublishAsync(It.Is<RedisChannel>(c => c == NotificationConstants.RedisNewNotificationChannel), It.Is<RedisValue>(v => v == json), CommandFlags.None), Times.Once);
    }
}
