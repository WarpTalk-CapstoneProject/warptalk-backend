using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.API.Middlewares;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Application.Mappers;
using Microsoft.Extensions.Logging;

namespace WarpTalk.NotificationService.API.GrpcServices;

/// <summary>
/// gRPC Service handling server-to-server commands.
/// Responsibilities:
/// - Persist newly created notifications to DB and publish events to Redis.
/// - Process MarkAsRead/MarkAllAsRead commands and update the database strictly before UI updates.
/// </summary>
public class NotificationGrpcServiceImpl : NotificationGrpcService.NotificationGrpcServiceBase
{
    private readonly INotificationService _notificationService;
    private readonly StackExchange.Redis.IConnectionMultiplexer _redis;
    private readonly ILogger<NotificationGrpcServiceImpl> _logger;
    private readonly WarpTalk.Shared.Email.IEmailTemplateSource _emailTemplates;
    private readonly WarpTalk.Shared.Email.IEmailDeliveryRecorder? _deliveries;

    public NotificationGrpcServiceImpl(
        INotificationService notificationService,
        StackExchange.Redis.IConnectionMultiplexer redis,
        ILogger<NotificationGrpcServiceImpl> logger,
        WarpTalk.Shared.Email.IEmailTemplateSource emailTemplates,
        WarpTalk.Shared.Email.IEmailDeliveryRecorder? deliveries = null)
    {
        _notificationService = notificationService;
        _redis = redis;
        _logger = logger;
        _emailTemplates = emailTemplates;
        _deliveries = deliveries;
    }

    /// <summary>
    /// The PUBLISHED version of a transactional email for the service that sends it, in the
    /// recipient's locale (falling back to English), with its layout and every block expanded.
    /// Drafts are never returned. An unknown key is answered "not found" rather than refused: the
    /// sender then uses its built-in wording, which is what it did before the CMS existed.
    /// </summary>
    public override async Task<GetEmailTemplateResponse> GetEmailTemplate(GetEmailTemplateRequest request, ServerCallContext context)
    {
        if (WarpTalk.Shared.Email.EmailTemplateCatalog.Find(request.TemplateKey) is null)
            return new GetEmailTemplateResponse { Found = false };

        var locale = string.IsNullOrWhiteSpace(request.Locale) ? null : request.Locale;
        var stored = await _emailTemplates.FindActiveAsync(request.TemplateKey, locale, context.CancellationToken);
        if (stored is null)
            return new GetEmailTemplateResponse { Found = false };

        return new GetEmailTemplateResponse
        {
            Found = true,
            Subject = stored.Subject,
            Heading = stored.Heading,
            BodyHtml = stored.BodyHtml,
            Version = stored.Version,
            Preheader = stored.Preheader,
            TextBody = stored.TextBody ?? string.Empty,
            LayoutHtml = stored.LayoutHtml ?? string.Empty,
            LayoutText = stored.LayoutText ?? string.Empty,
            LayoutDarkCss = stored.LayoutDarkCss ?? string.Empty,
            Locale = stored.Locale,
        };
    }

    /// <summary>One send reported by a sender, for the CMS's per-template delivery stats.</summary>
    public override async Task<RecordEmailDeliveryResponse> RecordEmailDelivery(RecordEmailDeliveryRequest request, ServerCallContext context)
    {
        if (_deliveries is null || WarpTalk.Shared.Email.EmailTemplateCatalog.Find(request.TemplateKey) is null)
            return new RecordEmailDeliveryResponse { Recorded = false };

        var email = new WarpTalk.Shared.Email.RenderedEmail(string.Empty, string.Empty, string.Empty)
        {
            TemplateKey = request.TemplateKey,
            Locale = WarpTalk.Shared.Email.EmailLocales.Normalize(request.Locale) ?? WarpTalk.Shared.Email.EmailLocales.Default,
            Version = request.Version,
        };
        await _deliveries.RecordAsync(email, request.Succeeded, context.CancellationToken);
        return new RecordEmailDeliveryResponse { Recorded = true };
    }

    public override async Task<SendNotificationResponse> SendNotification(SendNotificationRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            throw GrpcErrors.Required("User ID");

        if (!Guid.TryParse(request.UserId, out var parsedUserId))
            throw GrpcErrors.InvalidId("User");

        var meta = new Dictionary<string, string>();
        if (request.Metadata != null)
        {
            foreach (var kvp in request.Metadata) meta[kvp.Key] = kvp.Value;
        }



        var payloadJson = "{}";
        if (meta.Count > 0)
        {
            payloadJson = System.Text.Json.JsonSerializer.Serialize(meta);
        }

        var validationResult = NotificationValidator.Validate(request.Type, request.Title, request.Body, request.ActionUrl, payloadJson);
        if (!validationResult.IsSuccess)
        {
            _logger.LogWarning("Validation failed for creating notification. User: {UserId}, Type: {Type}, Error: {Error}, ErrorCode: {ErrorCode}", parsedUserId, request.Type, validationResult.Error, validationResult.ErrorCode);
            return new SendNotificationResponse
            {
                Success = false,
                NotificationId = ""
            };
        }

        var dto = NotificationMessageMapper.ToCreateDto(request, parsedUserId, payloadJson);
        var result = await _notificationService.CreateNotificationAsync(dto, context.CancellationToken);

        if (!result.IsSuccess || result.Value == null)
        {
            return new SendNotificationResponse
            {
                Success = false,
                NotificationId = ""
            };
        }

        try
        {
            var msg = WarpTalk.NotificationService.Application.Mappers.NotificationMessageMapper.ToRealtimeDto(result.Value, request.UserId);
            var json = System.Text.Json.JsonSerializer.Serialize(msg);
            await _redis.GetDatabase().PublishAsync(StackExchange.Redis.RedisChannel.Literal(NotificationConstants.RedisNewNotificationChannel), json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish real-time notification to Redis for notification ID: {NotificationId}, User ID: {UserId}", result.Value.Id, request.UserId);
        }

        return new SendNotificationResponse
        {
            Success = true,
            NotificationId = result.Value.Id.ToString()
        };
    }

    public override async Task<GetUserPreferencesResponse> GetUserPreferences(GetUserPreferencesRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.UserId, out var parsedUserId))
            throw GrpcErrors.InvalidId("User");

        var result = await _notificationService.GetPreferencesAsync(parsedUserId, context.CancellationToken);

        var response = new GetUserPreferencesResponse
        {
            UserId = request.UserId
        };

        if (result.IsSuccess && result.Value != null)
        {
            var pref = result.Value;
            response.Preferences.Add(new NotificationChannelPreference
            {
                Channel = pref.NotificationType ?? NotificationConstants.DefaultNotificationType,
                Enabled = pref.EmailEnabled || pref.PushEnabled || pref.InAppEnabled
            });
        }

        return response;
    }

    public override async Task<MarkAsReadResponse> MarkAsRead(MarkAsReadRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.UserId, out var userId))
            return new MarkAsReadResponse { Success = false, ErrorMessage = "Invalid User ID", ErrorCode = ErrorCodes.ValidationError };

        if (!Guid.TryParse(request.NotificationId, out var notificationId))
            return new MarkAsReadResponse { Success = false, ErrorMessage = "Invalid Notification ID", ErrorCode = ErrorCodes.ValidationError };

        var result = await _notificationService.MarkAsReadAsync(userId, notificationId, context.CancellationToken);

        return new MarkAsReadResponse
        {
            Success = result.IsSuccess,
            ErrorMessage = result.Error,
            ErrorCode = result.ErrorCode ?? ""
        };
    }

    public override async Task<MarkAllAsReadResponse> MarkAllAsRead(MarkAllAsReadRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.UserId, out var userId))
            return new MarkAllAsReadResponse { Success = false, ErrorMessage = "Invalid User ID", ErrorCode = ErrorCodes.ValidationError };

        var result = await _notificationService.MarkAllAsReadAsync(userId, context.CancellationToken);

        return new MarkAllAsReadResponse
        {
            Success = result.IsSuccess,
            ErrorMessage = result.Error,
            ErrorCode = result.ErrorCode ?? ""
        };
    }
}
