using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared.Email;

namespace WarpTalk.NotificationService.Application.Services;

/// <summary>
/// The template store itself, read directly. What the notification service's own senders use, and
/// what the GetEmailTemplate RPC answers every other service with.
/// </summary>
public sealed class DbEmailTemplateSource : IEmailTemplateSource
{
    private readonly IUnitOfWork _unitOfWork;

    public DbEmailTemplateSource(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<StoredEmailTemplate?> FindActiveAsync(string templateKey, CancellationToken ct = default)
    {
        var row = await _unitOfWork.NotificationTemplateRepository
            .GetActiveByTypeAsync(templateKey, EmailTemplateConstants.ChannelEmail, ct);
        return row is null
            ? null
            : new StoredEmailTemplate(row.Subject ?? string.Empty, row.Heading ?? string.Empty, row.BodyTemplate, row.Version);
    }
}
