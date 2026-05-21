using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.NotificationService.Application.Helpers.AdminNotifications;
using Xunit;

namespace WarpTalk.NotificationService.Tests.Application.Helpers.AdminNotifications;

public class NotificationValidationHelperTests
{
    [Fact]
    public void DeduplicateAndValidateUserIds_ShouldRemoveDuplicates()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var input = new List<Guid> { id1, id2, id1, id1 };

        var (isValid, result, errorMessage) = NotificationValidationHelper.DeduplicateAndValidateUserIds(input);

        Assert.True(isValid);
        Assert.Null(errorMessage);
        Assert.Equal(2, result.Count);
        Assert.Contains(id1, result);
        Assert.Contains(id2, result);
    }

    [Fact]
    public void DeduplicateAndValidateUserIds_ShouldReturnFalse_WhenExceedsMaxLimit()
    {
        var input = Enumerable.Range(0, 10001).Select(_ => Guid.NewGuid()).ToList();

        var (isValid, result, errorMessage) = NotificationValidationHelper.DeduplicateAndValidateUserIds(input);

        Assert.False(isValid);
        Assert.NotNull(errorMessage);
        Assert.Contains("exceeds the maximum limit", errorMessage);
        Assert.Null(result);
    }

    [Fact]
    public void DeduplicateAndValidateUserIds_ShouldReturnTrue_WhenExactlyMaxLimit()
    {
        var input = Enumerable.Range(0, 10000).Select(_ => Guid.NewGuid()).ToList();

        var (isValid, result, errorMessage) = NotificationValidationHelper.DeduplicateAndValidateUserIds(input);

        Assert.True(isValid);
        Assert.Null(errorMessage);
        Assert.Equal(10000, result.Count);
    }

    [Fact]
    public void DeduplicateAndValidateUserIds_ShouldReturnTrue_WhenNullOrEmpty()
    {
        var (isValid, result, errorMessage) = NotificationValidationHelper.DeduplicateAndValidateUserIds(null);
        
        Assert.True(isValid);
        Assert.Null(errorMessage);
        Assert.Empty(result);
    }
}
