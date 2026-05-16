using System;

namespace WarpTalk.NotificationService.Domain.Models;

public record AdminNotificationFilter(
    int Page = 1,
    int PageSize = 50,
    string? Title = null,
    string? Type = null,
    string? Status = null,
    DateTime? CreatedFrom = null,
    DateTime? CreatedTo = null
);
