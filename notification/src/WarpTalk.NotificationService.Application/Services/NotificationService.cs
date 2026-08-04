using WarpTalk.Shared;
using WarpTalk.NotificationService.Application.DTOs;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Application.Mappers;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace WarpTalk.NotificationService.Application.Services;

public class NotificationService : INotificationService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IEmailSender? _emailSender;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        IUnitOfWork unitOfWork,
        ILogger<NotificationService> logger,
        IEmailSender? emailSender = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _emailSender = emailSender;
    }

    public async Task<Result<NotificationPreferenceDto>> GetPreferencesAsync(Guid userId, CancellationToken ct = default)
    {
        var repo = _unitOfWork.NotificationPreferenceRepository;

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
        var repo = _unitOfWork.NotificationPreferenceRepository;
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

    public async Task<Result<NotificationPaginatedResponse>> GetNotificationsAsync(Guid userId, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        pageSize = Math.Max(1, Math.Min(pageSize, 100)); // Enforce bounded resource behavior
        var repo = _unitOfWork.NotificationMessageRepository;
        var (items, count) = await repo.GetPaginatedByUserIdAsync(userId, page, pageSize, ct);
        var unreadCount = await repo.CountAsync(notification =>
            notification.UserId == userId && !notification.IsRead);

        var dtoItems = items.Select(n => new NotificationMessageDto(
            n.Id, n.Type, n.Title, n.Content, n.ActionUrl, n.PayloadJson, n.IsRead, n.ReadAt, n.CreatedAt
        ));

        return Result.Success(new NotificationPaginatedResponse(dtoItems, count, unreadCount, page, pageSize));
    }

    public async Task<Result> MarkAsReadAsync(Guid userId, Guid notificationId, CancellationToken ct = default)
    {
        var repo = _unitOfWork.NotificationMessageRepository;
        var notification = await repo.GetByIdAndUserIdAsync(notificationId, userId, ct);

        if (notification == null)
            return Result.Failure("Notification not found", ErrorCodes.NotFound);

        if (!notification.IsRead)
        {
            await repo.MarkAsReadAsync(notificationId, userId, ct);
        }

        return Result.Success();
    }

    public async Task<Result> MarkAllAsReadAsync(Guid userId, CancellationToken ct = default)
    {
        await _unitOfWork.NotificationMessageRepository.MarkAllAsReadAsync(userId, ct);
        return Result.Success();
    }

    public async Task<Result<NotificationMessageDto>> CreateNotificationAsync(CreateNotificationMessageDto dto, CancellationToken ct = default)
    {
        var repo = _unitOfWork.NotificationMessageRepository;
        var notification = NotificationMessageMapper.ToEntity(dto);

        await repo.AddAsync(notification);
        await _unitOfWork.SaveChangesAsync();

        if (_emailSender != null)
        {
            try
            {
                var prefResult = await GetPreferencesAsync(dto.UserId, ct);
                if (prefResult.IsSuccess && prefResult.Value?.EmailEnabled == true)
                {
                    var userEmail = ExtractEmailFromPayload(dto.PayloadJson);
                    if (!string.IsNullOrWhiteSpace(userEmail))
                    {
                        var htmlBody = EmailTemplateRenderer.RenderGenericNotification(
                            dto.Title,
                            dto.Content,
                            dto.ActionUrl);
                        var delivered = await _emailSender.SendEmailAsync(
                            new EmailMessage(userEmail, dto.Title, htmlBody),
                            ct);
                        if (!delivered)
                        {
                            _logger.LogWarning(
                                "Email delivery was rejected for notification {NotificationId}",
                                notification.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Email delivery failed for notification {Id}", notification.Id);
            }
        }

        return Result.Success(NotificationMessageMapper.ToDto(notification));
    }

    private static string? ExtractEmailFromPayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("toEmail", out var prop) || doc.RootElement.TryGetProperty("email", out prop))
            {
                return prop.GetString();
            }
        }
        catch { }
        return null;
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
