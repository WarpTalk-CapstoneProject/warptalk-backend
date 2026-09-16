using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.NotificationService.Application.DTOs;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using Xunit;
using NotificationServiceImpl = WarpTalk.NotificationService.Application.Services.NotificationService;

namespace WarpTalk.NotificationService.Tests.Application.Services;

/// <summary>
/// GET and PUT /notifications/preferences must both work for a user who has no row yet —
/// PUT used to answer 404, so the settings page could not save for such a user.
/// </summary>
public class NotificationPreferencesTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<INotificationPreferenceRepository> _repo = new();
    private readonly NotificationServiceImpl _sut;

    public NotificationPreferencesTests()
    {
        _unitOfWork.Setup(u => u.NotificationPreferenceRepository).Returns(_repo.Object);
        _sut = new NotificationServiceImpl(_unitOfWork.Object, Mock.Of<ILogger<NotificationServiceImpl>>());
    }

    [Fact]
    public async Task GetPreferences_creates_default_row_when_missing()
    {
        var userId = Guid.NewGuid();
        NotificationPreference? added = null;
        _repo.Setup(r => r.GetByUserIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((NotificationPreference?)null);
        _repo.Setup(r => r.AddAsync(It.IsAny<NotificationPreference>())).Callback<NotificationPreference>(p => added = p).Returns(Task.CompletedTask);

        var result = await _sut.GetPreferencesAsync(userId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(added);
        Assert.Equal(userId, added!.UserId);
        Assert.Equal(NotificationConstants.DefaultNotificationType, added.NotificationType);
        Assert.True(result.Value!.EmailEnabled);
        Assert.True(result.Value.PushEnabled);
        Assert.True(result.Value.InAppEnabled);
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task GetPreferences_returns_existing_row_without_writing()
    {
        var userId = Guid.NewGuid();
        var existing = new NotificationPreference { Id = Guid.NewGuid(), UserId = userId, NotificationType = "SYSTEM", EmailEnabled = false, PushEnabled = true, InAppEnabled = true };
        _repo.Setup(r => r.GetByUserIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await _sut.GetPreferencesAsync(userId);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.EmailEnabled);
        _repo.Verify(r => r.AddAsync(It.IsAny<NotificationPreference>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Never);
    }

    [Fact]
    public async Task UpdatePreferences_creates_row_with_defaults_and_applies_patch_when_missing()
    {
        var userId = Guid.NewGuid();
        NotificationPreference? added = null;
        _repo.Setup(r => r.GetByUserIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((NotificationPreference?)null);
        _repo.Setup(r => r.AddAsync(It.IsAny<NotificationPreference>())).Callback<NotificationPreference>(p => added = p).Returns(Task.CompletedTask);

        var result = await _sut.UpdatePreferencesAsync(userId, new UpdateNotificationPreferenceRequest(EmailEnabled: false, PushEnabled: null, InAppEnabled: null));

        Assert.True(result.IsSuccess);
        Assert.NotNull(added);
        Assert.False(added!.EmailEnabled);
        // Untouched channels keep the entity defaults.
        Assert.True(added.PushEnabled);
        Assert.True(added.InAppEnabled);
        Assert.False(result.Value!.EmailEnabled);
        // One save for insert + patch; a freshly added entity is not also marked modified.
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
        _repo.Verify(r => r.Update(It.IsAny<NotificationPreference>()), Times.Never);
    }

    [Fact]
    public async Task UpdatePreferences_patches_existing_row()
    {
        var userId = Guid.NewGuid();
        var existing = new NotificationPreference { Id = Guid.NewGuid(), UserId = userId, NotificationType = "SYSTEM", EmailEnabled = true, PushEnabled = true, InAppEnabled = true };
        _repo.Setup(r => r.GetByUserIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await _sut.UpdatePreferencesAsync(userId, new UpdateNotificationPreferenceRequest(null, PushEnabled: false, InAppEnabled: null));

        Assert.True(result.IsSuccess);
        Assert.True(existing.EmailEnabled);
        Assert.False(existing.PushEnabled);
        _repo.Verify(r => r.AddAsync(It.IsAny<NotificationPreference>()), Times.Never);
        _repo.Verify(r => r.Update(existing), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
    }
}
