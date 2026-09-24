using WarpTalk.Shared;
using WarpTalk.Shared.Email;
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
    private readonly IEmailTemplateComposer _emailTemplates;
    private readonly ILogger<NotificationService> _logger;

    /// <summary>Where an email copy's button points when the notification's own link is unsafe.</summary>
    public const string FallbackActionUrl = "https://warptalk.app";

    public NotificationService(
        IUnitOfWork unitOfWork,
        ILogger<NotificationService> logger,
        IEmailSender? emailSender = null,
        IEmailTemplateComposer? emailTemplates = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _emailSender = emailSender;
        // The email copy is the admin-editable "notification.email-copy" template, read from this
        // service's own store.
        _emailTemplates = emailTemplates ?? new EmailTemplateComposer(new DbEmailTemplateSource(unitOfWork));
    }

    public async Task<Result<NotificationPreferenceDto>> GetPreferencesAsync(Guid userId, CancellationToken ct = default)
    {
        var (pref, _) = await GetOrCreatePreferenceAsync(userId, ct);
        return Result.Success(NotificationPreferenceMapper.ToDto(pref));
    }

    public async Task<Result<NotificationPreferenceDto>> UpdatePreferencesAsync(Guid userId, UpdateNotificationPreferenceRequest request, CancellationToken ct = default)
    {
        // A user with no row used to get 404 here, so the settings page could never save for
        // anyone whose row had not been lazily created by an earlier GET. PUT now creates the
        // default row and applies the patch to it in the same SaveChanges.
        var (pref, created) = await GetOrCreatePreferenceAsync(userId, ct, saveIfCreated: false);

        NotificationPreferenceMapper.ApplyUpdate(pref, request);
        // Update() on a freshly added entity is unnecessary; only mark an existing row modified.
        if (!created) _unitOfWork.NotificationPreferenceRepository.Update(pref);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(NotificationPreferenceMapper.ToDto(pref));
    }

    /// <summary>
    /// Loads the user's preference row, creating it with the entity defaults (every channel on,
    /// type SYSTEM — the only type ever written, which keeps the (user_id, notification_type)
    /// unique key satisfied) when the user has none yet.
    /// </summary>
    private async Task<(NotificationPreference Pref, bool Created)> GetOrCreatePreferenceAsync(
        Guid userId, CancellationToken ct, bool saveIfCreated = true)
    {
        var repo = _unitOfWork.NotificationPreferenceRepository;
        var pref = await repo.GetByUserIdAsync(userId, ct);
        if (pref != null) return (pref, false);

        pref = NotificationPreferenceMapper.CreateDefaultEntity(userId);
        await repo.AddAsync(pref);
        if (saveIfCreated) await _unitOfWork.SaveChangesAsync();
        return (pref, true);
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
                        var email = await _emailTemplates.ComposeAsync(
                            EmailTemplateCatalog.NotificationEmailCopy,
                            new Dictionary<string, string>
                            {
                                ["Title"] = dto.Title,
                                ["Content"] = dto.Content,
                                ["ActionUrl"] = SafeActionUrl(dto.ActionUrl),
                            },
                            ct);
                        var delivered = await _emailSender.SendEmailAsync(
                            new EmailMessage(userEmail, email.Subject, email.HtmlBody, TextBody: email.TextBody),
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

    /// <summary>
    /// A notification's link, only if it is a web address. A producer's action_url is not trusted
    /// to become an href in somebody's inbox.
    /// </summary>
    public static string SafeActionUrl(string? actionUrl) =>
        Uri.TryCreate(actionUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.ToString()
            : FallbackActionUrl;

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
}
