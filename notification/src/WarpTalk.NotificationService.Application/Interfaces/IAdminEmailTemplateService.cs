using WarpTalk.NotificationService.Application.DTOs.EmailTemplates;
using WarpTalk.Shared;

namespace WarpTalk.NotificationService.Application.Interfaces;

/// <summary>
/// The email template CMS. Every template here is one a sender reads through
/// <see cref="WarpTalk.Shared.Email.IEmailTemplateComposer"/>; nothing can be saved for an email
/// the platform does not send.
/// </summary>
public interface IAdminEmailTemplateService
{
    Task<Result<IReadOnlyList<EmailTemplateSummaryDto>>> ListAsync(CancellationToken ct = default);
    Task<Result<EmailTemplateDetailDto>> GetAsync(string key, CancellationToken ct = default);
    Task<Result<EmailTemplateDetailDto>> SaveAsync(Guid adminId, string key, SaveEmailTemplateRequest request, CancellationToken ct = default);
    Task<Result<EmailTemplateDetailDto>> ResetAsync(Guid adminId, string key, CancellationToken ct = default);
    Task<Result<IReadOnlyList<EmailTemplateVersionDto>>> ListVersionsAsync(string key, CancellationToken ct = default);
    Task<Result<EmailTemplateDetailDto>> RestoreAsync(Guid adminId, string key, int version, CancellationToken ct = default);
    Result<EmailTemplatePreviewDto> Preview(string key, EmailTemplateDraftRequest draft);
    Task<Result<EmailTemplateTestSendDto>> SendTestAsync(string key, EmailTemplateDraftRequest draft, string? adminEmail, CancellationToken ct = default);
}
