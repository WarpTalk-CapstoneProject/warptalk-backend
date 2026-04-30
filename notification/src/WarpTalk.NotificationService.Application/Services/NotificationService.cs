using WarpTalk.Shared;
using WarpTalk.NotificationService.Application.DTOs;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Application.Services;

public class NotificationService : INotificationService
{
    private readonly IUnitOfWork _unitOfWork;

    public NotificationService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<NotificationPreferenceDto>> GetPreferencesAsync(Guid userId, CancellationToken ct = default)
    {
        var repo = _unitOfWork.Repository<NotificationPreference>();
        
        // We do a simple fallback if multiple matching items exist
        // Real implementation usually handles SingleOrDefault correctly
        var prefs = await repo.FindAsync(p => p.UserId == userId);
        var pref = prefs.FirstOrDefault();
        
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
            await repo.AddAsync(pref);
            await _unitOfWork.SaveChangesAsync();
        }

        return Result.Success(MapToDto(pref));
    }

    public async Task<Result<NotificationPreferenceDto>> UpdatePreferencesAsync(Guid userId, UpdateNotificationPreferenceRequest request, CancellationToken ct = default)
    {
        var repo = _unitOfWork.Repository<NotificationPreference>();
        var prefs = await repo.FindAsync(p => p.UserId == userId);
        var pref = prefs.FirstOrDefault();

        if (pref == null)
            return Result.Failure<NotificationPreferenceDto>("Preferences not found", ErrorCodes.NotFound);

        if (request.EmailEnabled.HasValue) pref.EmailEnabled = request.EmailEnabled.Value;
        if (request.PushEnabled.HasValue) pref.PushEnabled = request.PushEnabled.Value;
        if (request.InAppEnabled.HasValue) pref.InAppEnabled = request.InAppEnabled.Value;

        pref.UpdatedAt = DateTime.UtcNow;
        repo.Update(pref);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(MapToDto(pref));
    }

    public async Task<Result> SendNotificationAsync(Guid userId, string templateCode, Dictionary<string, string> variables, CancellationToken ct = default)
    {
        // Mock notification send logic
        await Task.Delay(100, ct);
        return Result.Success();
    }

    public async Task<Result<NotificationPaginatedResponse>> GetNotificationsAsync(Guid userId, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var repo = _unitOfWork.Repository<NotificationMessage>();
        var count = await repo.CountAsync(n => n.UserId == userId);
        var items = await repo.FindWithPaginationAsync(
            n => n.UserId == userId, 
            (page - 1) * pageSize, 
            pageSize, 
            q => q.OrderByDescending(n => n.CreatedAt)
        );

        var dtoItems = items.Select(n => new NotificationMessageDto(
            n.Id, n.Type, n.Title, n.Content, n.PayloadJson, n.IsRead, n.ReadAt, n.CreatedAt
        ));

        return Result.Success(new NotificationPaginatedResponse(dtoItems, count, page, pageSize));
    }

    public async Task<Result> MarkAsReadAsync(Guid userId, Guid notificationId, CancellationToken ct = default)
    {
        var repo = _unitOfWork.Repository<NotificationMessage>();
        var notification = await repo.GetByIdAsync(notificationId);
        
        if (notification == null)
            return Result.Failure("Notification not found", ErrorCodes.NotFound);
            
        if (notification.UserId != userId)
            return Result.Failure("Forbidden access", ErrorCodes.Forbidden);
            
        if (!notification.IsRead)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
            repo.Update(notification);
            await _unitOfWork.SaveChangesAsync();
        }
        
        return Result.Success();
    }

    public async Task<Result> MarkAllAsReadAsync(Guid userId, CancellationToken ct = default)
    {
        var repo = _unitOfWork.Repository<NotificationMessage>();
        var unreadItems = await repo.FindAsync(n => n.UserId == userId && !n.IsRead);
        
        var now = DateTime.UtcNow;
        foreach (var item in unreadItems)
        {
            item.IsRead = true;
            item.ReadAt = now;
            repo.Update(item);
        }
        
        if (unreadItems.Any())
        {
            await _unitOfWork.SaveChangesAsync();
        }
        
        return Result.Success();
    }

    public async Task<Result<NotificationMessageDto>> CreateNotificationAsync(Guid userId, string type, string title, string content, string payloadJson, CancellationToken ct = default)
    {
        var validationResult = WarpTalk.NotificationService.Application.Validators.NotificationValidator.Validate(type, title, content, payloadJson);
        if (!validationResult.IsSuccess)
        {
            return Result.Failure<NotificationMessageDto>(validationResult.Error, validationResult.ErrorCode);
        }

        var repo = _unitOfWork.Repository<NotificationMessage>();
        
        var notification = new NotificationMessage
        {
            UserId = userId,
            Type = type,
            Title = title,
            Content = content,
            PayloadJson = payloadJson,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };
        
        await repo.AddAsync(notification);
        await _unitOfWork.SaveChangesAsync();
        
        var dto = new NotificationMessageDto(
            notification.Id, notification.Type, notification.Title, notification.Content, notification.PayloadJson, notification.IsRead, notification.ReadAt, notification.CreatedAt
        );
        
        return Result.Success(dto);
    }

    private NotificationPreferenceDto MapToDto(NotificationPreference p) =>
        new NotificationPreferenceDto(
            p.Id,
            p.UserId,
            p.NotificationType ?? "SYSTEM",
            p.EmailEnabled,
            p.PushEnabled,
            p.InAppEnabled,
            p.UpdatedAt
        );
}
