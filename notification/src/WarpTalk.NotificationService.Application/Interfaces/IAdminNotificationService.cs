using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.NotificationService.Application.DTOs.AdminNotifications;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.Shared;

namespace WarpTalk.NotificationService.Application.Interfaces;

public interface IAdminNotificationService
{
    Task<Result<AdminNotification>> CreateAdminNotificationAsync(Guid adminId, CreateAdminNotificationDto request, CancellationToken ct = default);
    Task<Result<AdminNotificationPaginatedResponse>> GetAdminNotificationsAsync(GetAdminNotificationsQuery query, CancellationToken ct = default);
    Task<Result<AdminNotificationDetailDto>> GetAdminNotificationDetailAsync(Guid id, CancellationToken ct = default);
}
