using System;
using System.Collections.Generic;
using System.Linq;

namespace WarpTalk.NotificationService.Application.Helpers.AdminNotifications;

public static class NotificationValidationHelper
{
    private const int MaxSpecificUsers = 10000;

    public static (bool IsValid, List<Guid>? UserIds, string? ErrorMessage) DeduplicateAndValidateUserIds(IEnumerable<Guid>? inputUserIds)
    {
        if (inputUserIds == null || !inputUserIds.Any())
        {
            return (true, new List<Guid>(), null);
        }

        var deduplicatedList = inputUserIds.Distinct().ToList();

        if (deduplicatedList.Count > MaxSpecificUsers)
        {
            return (false, null, $"The number of unique specific users ({deduplicatedList.Count}) exceeds the maximum limit of {MaxSpecificUsers}.");
        }

        return (true, deduplicatedList, null);
    }
}
