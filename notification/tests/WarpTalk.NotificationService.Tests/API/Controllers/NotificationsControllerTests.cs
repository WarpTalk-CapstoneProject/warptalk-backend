using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.NotificationService.API.Controllers;
using WarpTalk.NotificationService.Application.DTOs;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;
using NotificationServiceImpl = global::WarpTalk.NotificationService.Application.Services.NotificationService;

namespace WarpTalk.NotificationService.Tests.API.Controllers;

/// <summary>
/// Unit tests for NotificationsController.GetNotifications (Function 60) and
/// NotificationsController.MarkAsRead (Function 61). The controller is wired to the real
/// NotificationService over a mocked unit of work so that clamping, unread counting and
/// ownership lookups are exercised end to end without a database.
/// </summary>
public class NotificationsControllerTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork = new();
    private readonly Mock<INotificationMessageRepository> _mockRepo = new();
    private readonly Mock<IAdminNotificationService> _mockAdminService = new();
    private readonly NotificationServiceImpl _service;

    public NotificationsControllerTests()
    {
        _mockUnitOfWork.Setup(u => u.NotificationMessageRepository).Returns(_mockRepo.Object);
        _service = new NotificationServiceImpl(
            _mockUnitOfWork.Object,
            new Mock<ILogger<NotificationServiceImpl>>().Object);
    }

    private NotificationsController CreateController(INotificationService service, string? nameIdentifier)
    {
        var claims = new List<Claim>();
        if (nameIdentifier != null)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, nameIdentifier));

        var controller = new NotificationsController(
            service,
            _mockAdminService.Object,
            new Mock<ILogger<NotificationsController>>().Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
        return controller;
    }

    private static NotificationMessage Message(Guid userId, bool isRead = false) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Type = "SYSTEM",
        Title = "Title",
        Content = "Content",
        PayloadJson = "{}",
        IsRead = isRead,
        CreatedAt = DateTime.UtcNow
    };

    // ---------------------------------------------------------------------
    // Function 60 - Get Notifications
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetNotifications_UTCID01_ValidClaim_ReturnsOkWithPaginatedDtosAndUnreadCount()
    {
        var userId = Guid.NewGuid();
        var items = new[] { Message(userId), Message(userId, isRead: true) };
        _mockRepo.Setup(r => r.GetPaginatedByUserIdAsync(userId, 1, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync((items, 2));
        _mockRepo.Setup(r => r.CountAsync(It.IsAny<Expression<Func<NotificationMessage, bool>>>()))
            .ReturnsAsync(1);

        var controller = CreateController(_service, userId.ToString());
        var result = await controller.GetNotifications(1, 50, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<NotificationPaginatedResponse>(ok.Value);
        Assert.Equal(2, body.Items.Count());
        Assert.Equal(items[0].Id, body.Items.First().Id);
        Assert.Equal(2, body.TotalCount);
        Assert.Equal(1, body.UnreadCount);
        Assert.Equal(1, body.Page);
        Assert.Equal(50, body.PageSize);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task GetNotifications_UTCID02_MissingOrInvalidClaim_ReturnsUnauthorized(string? claim)
    {
        var service = new Mock<INotificationService>();
        var controller = CreateController(service.Object, claim);

        var result = await controller.GetNotifications(1, 50, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        service.Verify(s => s.GetNotificationsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task GetNotifications_UTCID03_PageBelowOne_IsClampedToOne(int page)
    {
        var userId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetPaginatedByUserIdAsync(userId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<NotificationMessage>(), 0));

        var controller = CreateController(_service, userId.ToString());
        var result = await controller.GetNotifications(page, 50, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<NotificationPaginatedResponse>(ok.Value);
        Assert.Equal(1, body.Page);
        _mockRepo.Verify(r => r.GetPaginatedByUserIdAsync(userId, 1, 50, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetNotifications_UTCID04_PageSizeAbove100_IsClampedTo100()
    {
        var userId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetPaginatedByUserIdAsync(userId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<NotificationMessage>(), 0));

        var controller = CreateController(_service, userId.ToString());
        var result = await controller.GetNotifications(1, 500, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<NotificationPaginatedResponse>(ok.Value);
        Assert.Equal(100, body.PageSize);
        _mockRepo.Verify(r => r.GetPaginatedByUserIdAsync(userId, 1, 100, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetNotifications_UTCID05_UnreadCountIsCalculatedSeparatelyFromPageItems()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        // The only item on the page is already read, yet the user has unread items elsewhere.
        var items = new[] { Message(userId, isRead: true) };
        _mockRepo.Setup(r => r.GetPaginatedByUserIdAsync(userId, 2, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((items, 10));

        Expression<Func<NotificationMessage, bool>>? captured = null;
        _mockRepo.Setup(r => r.CountAsync(It.IsAny<Expression<Func<NotificationMessage, bool>>>()))
            .Callback<Expression<Func<NotificationMessage, bool>>>(p => captured = p)
            .ReturnsAsync(7);

        var controller = CreateController(_service, userId.ToString());
        var result = await controller.GetNotifications(2, 1, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<NotificationPaginatedResponse>(ok.Value);
        Assert.Single(body.Items);
        Assert.Equal(7, body.UnreadCount);
        _mockRepo.Verify(r => r.CountAsync(It.IsAny<Expression<Func<NotificationMessage, bool>>>()), Times.Once);

        Assert.NotNull(captured);
        var predicate = captured!.Compile();
        Assert.True(predicate(Message(userId, isRead: false)));
        Assert.False(predicate(Message(userId, isRead: true)));
        Assert.False(predicate(Message(otherUserId, isRead: false)));
    }

    [Fact]
    public async Task GetNotifications_UTCID06_PageBeyondAvailable_ReturnsEmptyItemsSuccess()
    {
        var userId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetPaginatedByUserIdAsync(userId, 99, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<NotificationMessage>(), 3));
        _mockRepo.Setup(r => r.CountAsync(It.IsAny<Expression<Func<NotificationMessage, bool>>>()))
            .ReturnsAsync(2);

        var controller = CreateController(_service, userId.ToString());
        var result = await controller.GetNotifications(99, 50, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<NotificationPaginatedResponse>(ok.Value);
        Assert.Empty(body.Items);
        Assert.Equal(3, body.TotalCount);
        Assert.Equal(2, body.UnreadCount);
        Assert.Equal(99, body.Page);
    }

    [Fact]
    public async Task GetNotifications_UTCID07_NoNotificationsForUser_ReturnsEmptySuccess()
    {
        var userId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetPaginatedByUserIdAsync(userId, 1, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<NotificationMessage>(), 0));
        _mockRepo.Setup(r => r.CountAsync(It.IsAny<Expression<Func<NotificationMessage, bool>>>()))
            .ReturnsAsync(0);

        var controller = CreateController(_service, userId.ToString());
        var result = await controller.GetNotifications(1, 50, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<NotificationPaginatedResponse>(ok.Value);
        Assert.Empty(body.Items);
        Assert.Equal(0, body.TotalCount);
        Assert.Equal(0, body.UnreadCount);
    }

    // ---------------------------------------------------------------------
    // Function 61 - Mark Notification Read
    // ---------------------------------------------------------------------

    [Fact]
    public async Task MarkAsRead_UTCID01_OwnUnreadNotification_ReturnsNoContentAndMarksRead()
    {
        var userId = Guid.NewGuid();
        var notification = Message(userId, isRead: false);
        _mockRepo.Setup(r => r.GetByIdAndUserIdAsync(notification.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(notification);
        _mockRepo.Setup(r => r.MarkAsReadAsync(notification.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var controller = CreateController(_service, userId.ToString());
        var result = await controller.MarkAsRead(notification.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        _mockRepo.Verify(r => r.MarkAsReadAsync(notification.Id, userId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task MarkAsRead_UTCID02_MissingOrInvalidClaim_ReturnsUnauthorized(string? claim)
    {
        var service = new Mock<INotificationService>();
        var controller = CreateController(service.Object, claim);

        var result = await controller.MarkAsRead(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        service.Verify(s => s.MarkAsReadAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MarkAsRead_UTCID03_NotificationDoesNotExist_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetByIdAndUserIdAsync(notificationId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((NotificationMessage?)null);

        var controller = CreateController(_service, userId.ToString());
        var result = await controller.MarkAsRead(notificationId, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var error = Assert.IsType<ApiErrorResponse>(notFound.Value);
        Assert.Equal(ErrorCodes.NotFound, error.Code);
        _mockRepo.Verify(r => r.MarkAsReadAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MarkAsRead_UTCID04_NotificationBelongsToAnotherUser_ReturnsNotFound()
    {
        var callerId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var notification = Message(ownerId);
        // The repository only matches on (id, userId), so a lookup by the caller finds nothing.
        _mockRepo.Setup(r => r.GetByIdAndUserIdAsync(notification.Id, ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(notification);
        _mockRepo.Setup(r => r.GetByIdAndUserIdAsync(notification.Id, callerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((NotificationMessage?)null);

        var controller = CreateController(_service, callerId.ToString());
        var result = await controller.MarkAsRead(notification.Id, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.False(notification.IsRead);
        _mockRepo.Verify(r => r.MarkAsReadAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MarkAsRead_UTCID05_AlreadyRead_ReturnsNoContentWithoutUpdate()
    {
        var userId = Guid.NewGuid();
        var notification = Message(userId, isRead: true);
        _mockRepo.Setup(r => r.GetByIdAndUserIdAsync(notification.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(notification);

        var controller = CreateController(_service, userId.ToString());
        var result = await controller.MarkAsRead(notification.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        _mockRepo.Verify(r => r.MarkAsReadAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(), Times.Never);
    }

    [Fact]
    public async Task MarkAsRead_UTCID06_EmptyGuid_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetByIdAndUserIdAsync(Guid.Empty, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((NotificationMessage?)null);

        var controller = CreateController(_service, userId.ToString());
        var result = await controller.MarkAsRead(Guid.Empty, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        _mockRepo.Verify(r => r.GetByIdAndUserIdAsync(Guid.Empty, userId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkAsRead_UTCID07_RepositoryThrows_ExceptionPropagatesFromServiceAndController()
    {
        var userId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetByIdAndUserIdAsync(notificationId, userId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var serviceEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.MarkAsReadAsync(userId, notificationId));
        Assert.Equal("database unavailable", serviceEx.Message);

        var controller = CreateController(_service, userId.ToString());
        var controllerEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.MarkAsRead(notificationId, CancellationToken.None));
        Assert.Equal("database unavailable", controllerEx.Message);
    }
}
