using Microsoft.Extensions.Logging;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared.Email;

namespace WarpTalk.NotificationService.Application.Services.EmailCms;

/// <summary>
/// The delivery counter itself. Every sender reports through here: this service's own email copy
/// directly, auth / workspace / translation-room over the RecordEmailDelivery RPC.
/// </summary>
public sealed class DbEmailDeliveryRecorder : IEmailDeliveryRecorder
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;
    private readonly ILogger<DbEmailDeliveryRecorder> _logger;

    public DbEmailDeliveryRecorder(IUnitOfWork unitOfWork, ILogger<DbEmailDeliveryRecorder> logger, TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task RecordAsync(RenderedEmail email, bool succeeded, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(email.TemplateKey) || EmailTemplateCatalog.Find(email.TemplateKey) is null) return;
        try
        {
            var locale = EmailLocales.Normalize(email.Locale) ?? EmailLocales.Default;
            var day = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
            await _unitOfWork.EmailDeliveryStatRepository.IncrementAsync(email.TemplateKey, locale, day, succeeded, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not count a delivery of {TemplateKey}.", email.TemplateKey);
        }
    }
}
