using WarpTalk.Shared;
using WarpTalk.NotificationService.Application.DTOs;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace WarpTalk.NotificationService.Application.Services;

public class NotificationService : INotificationService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(IUnitOfWork unitOfWork, ILogger<NotificationService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<NotificationPreferenceDto>> GetPreferencesAsync(Guid userId, CancellationToken ct = default)
    {
        var pref = await _unitOfWork.NotificationPreferenceRepository.GetByUserIdAsync(userId, ct);
        
        if (pref == null)
        {
            pref = new NotificationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                NotificationType = "SYSTEM",
                EmailEnabled = true,
                PushEnabled = true,
                InAppEnabled = true,
                UpdatedAt = DateTime.UtcNow
            };
            await _unitOfWork.NotificationPreferenceRepository.AddAsync(pref);
            await _unitOfWork.SaveChangesAsync();
        }

        return Result.Success(pref.ToDto());
    }

    public async Task<Result<NotificationPreferenceDto>> UpdatePreferencesAsync(Guid userId, UpdateNotificationPreferenceRequest request, CancellationToken ct = default)
    {
        var pref = await _unitOfWork.NotificationPreferenceRepository.GetByUserIdAsync(userId, ct);

        if (pref == null)
            return Result.Failure<NotificationPreferenceDto>("Preferences not found", ErrorCodes.NotFound);

        if (request.EmailEnabled.HasValue) pref.EmailEnabled = request.EmailEnabled.Value;
        if (request.PushEnabled.HasValue) pref.PushEnabled = request.PushEnabled.Value;
        if (request.InAppEnabled.HasValue) pref.InAppEnabled = request.InAppEnabled.Value;

        pref.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.NotificationPreferenceRepository.Update(pref);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(pref.ToDto());
    }

    public async Task<Result> SendNotificationAsync(Guid userId, string templateCode, Dictionary<string, string> variables, CancellationToken ct = default)
    {
        // Mock notification send logic
        await Task.Delay(100, ct);
        return Result.Success();
    }

    public async Task<Result<NotificationPaginatedResponse>> GetNotificationsAsync(Guid userId, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        pageSize = Math.Max(1, Math.Min(pageSize, 100)); // Enforce bounded resource behavior
        var (items, count) = await _unitOfWork.NotificationMessageRepository.GetPaginatedByUserIdAsync(userId, page, pageSize, ct);

        var dtoItems = items.Select(n => new NotificationMessageDto(
            n.Id, n.Type, n.Title, n.Content, n.ActionUrl, n.PayloadJson, n.IsRead, n.ReadAt, n.CreatedAt
        ));

        return Result.Success(new NotificationPaginatedResponse(dtoItems, count, page, pageSize));
    }

    public async Task<Result> MarkAsReadAsync(Guid userId, Guid notificationId, CancellationToken ct = default)
    {
        var notification = await _unitOfWork.NotificationMessageRepository.GetByIdAndUserIdAsync(notificationId, userId, ct);
        
        if (notification == null)
            return Result.Failure("Notification not found", ErrorCodes.NotFound);
            
        if (!notification.IsRead)
        {
            await _unitOfWork.NotificationMessageRepository.MarkAsReadAsync(notificationId, userId, ct);
        }
        
        return Result.Success();
    }

    public async Task<Result> MarkAllAsReadAsync(Guid userId, CancellationToken ct = default)
    {
        await _unitOfWork.NotificationMessageRepository.MarkAllAsReadAsync(userId, ct);
        return Result.Success();
    }

    public async Task<Result<NotificationMessageDto>> CreateNotificationAsync(Guid userId, string type, string title, string content, string? actionUrl, string payloadJson, CancellationToken ct = default)
    {

        var notification = new NotificationMessage
        {
            UserId = userId,
            Type = type,
            Title = title,
            Content = content,
            ActionUrl = actionUrl,
            PayloadJson = payloadJson,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };
        
        await _unitOfWork.NotificationMessageRepository.AddAsync(notification);
        await _unitOfWork.SaveChangesAsync();
        
        var dto = new NotificationMessageDto(
            notification.Id, notification.Type, notification.Title, notification.Content, notification.ActionUrl, notification.PayloadJson, notification.IsRead, notification.ReadAt, notification.CreatedAt
        );
        
        return Result.Success(dto);
    }

}
