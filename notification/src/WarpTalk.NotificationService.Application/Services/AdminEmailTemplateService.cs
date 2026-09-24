using System.Text.Json;
using Microsoft.Extensions.Logging;
using WarpTalk.NotificationService.Application.DTOs.EmailTemplates;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Email;

namespace WarpTalk.NotificationService.Application.Services;

/// <summary>
/// The email template CMS.
///
/// The template list is <see cref="EmailTemplateCatalog"/>, not the table: a row can only exist
/// for an email a sender actually composes through <see cref="IEmailTemplateComposer"/>, and every
/// such email is listed whether or not anyone has edited it. The table holds the current edit per
/// email (<c>notification_templates</c>, channel EMAIL) and <c>notification_template_versions</c>
/// holds every save, restore and reset.
/// </summary>
public sealed class AdminEmailTemplateService : IAdminEmailTemplateService
{
    private const int MaxVersionsListed = 50;
    private const string Channel = EmailTemplateConstants.ChannelEmail;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IEmailSender? _emailSender;
    private readonly TimeProvider _time;
    private readonly ILogger<AdminEmailTemplateService> _logger;

    public AdminEmailTemplateService(
        IUnitOfWork unitOfWork,
        ILogger<AdminEmailTemplateService> logger,
        IEmailSender? emailSender = null,
        TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _emailSender = emailSender;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<IReadOnlyList<EmailTemplateSummaryDto>>> ListAsync(CancellationToken ct = default)
    {
        var rows = (await _unitOfWork.NotificationTemplateRepository.ListByChannelAsync(Channel, ct))
            .ToDictionary(row => row.Type, StringComparer.Ordinal);

        IReadOnlyList<EmailTemplateSummaryDto> items = EmailTemplateCatalog.All
            .Select(definition => Summary(definition, rows.GetValueOrDefault(definition.Key)))
            .ToList();
        return Result.Success(items);
    }

    public async Task<Result<EmailTemplateDetailDto>> GetAsync(string key, CancellationToken ct = default)
    {
        var definition = EmailTemplateCatalog.Find(key);
        if (definition is null) return UnknownTemplate<EmailTemplateDetailDto>(key);

        var row = await _unitOfWork.NotificationTemplateRepository.GetByTypeAsync(key, Channel, ct);
        return Result.Success(Detail(definition, row));
    }

    public async Task<Result<EmailTemplateDetailDto>> SaveAsync(
        Guid adminId, string key, SaveEmailTemplateRequest request, CancellationToken ct = default)
    {
        var definition = EmailTemplateCatalog.Find(key);
        if (definition is null) return UnknownTemplate<EmailTemplateDetailDto>(key);

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > EmailTemplateConstants.MaxNoteLength })
            return Result.Failure<EmailTemplateDetailDto>(
                $"The note must be {EmailTemplateConstants.MaxNoteLength} characters or fewer.", ErrorCodes.ValidationError);

        return await WriteAsync(
            adminId, definition, Content(request.Subject, request.Heading, request.BodyHtml),
            EmailTemplateConstants.ActionSaved, restoredFrom: null, note, request.ExpectedVersion, ct);
    }

    public async Task<Result<EmailTemplateDetailDto>> RestoreAsync(
        Guid adminId, string key, int version, CancellationToken ct = default)
    {
        var definition = EmailTemplateCatalog.Find(key);
        if (definition is null) return UnknownTemplate<EmailTemplateDetailDto>(key);

        var entry = await _unitOfWork.NotificationTemplateVersionRepository.GetAsync(key, Channel, version, ct);
        if (entry is null)
            return Result.Failure<EmailTemplateDetailDto>($"Version {version} of this email does not exist.", ErrorCodes.NotFound);

        return await WriteAsync(
            adminId, definition, Content(entry.Subject, entry.Heading, entry.BodyTemplate),
            EmailTemplateConstants.ActionRestored, restoredFrom: version, note: null, expectedVersion: null, ct);
    }

    public async Task<Result<EmailTemplateDetailDto>> ResetAsync(Guid adminId, string key, CancellationToken ct = default)
    {
        var definition = EmailTemplateCatalog.Find(key);
        if (definition is null) return UnknownTemplate<EmailTemplateDetailDto>(key);

        var row = await _unitOfWork.NotificationTemplateRepository.GetByTypeAsync(key, Channel, ct);
        if (row is null || !row.IsActive)
            return Result.Success(Detail(definition, row)); // Already the default; nothing to record.

        var now = _time.GetUtcNow().UtcDateTime;
        row.IsActive = false;
        row.Version += 1;
        row.UpdatedAt = now;
        row.UpdatedBy = adminId;

        await _unitOfWork.NotificationTemplateVersionRepository.AddAsync(
            VersionEntry(key, row.Version, EmailTemplateConstants.ActionReset, null, definition.Default, null, adminId, now), ct);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Email template {TemplateKey} reset to default by {AdminId} (v{Version}).", key, adminId, row.Version);
        return Result.Success(Detail(definition, row));
    }

    public async Task<Result<IReadOnlyList<EmailTemplateVersionDto>>> ListVersionsAsync(string key, CancellationToken ct = default)
    {
        if (EmailTemplateCatalog.Find(key) is null) return UnknownTemplate<IReadOnlyList<EmailTemplateVersionDto>>(key);

        var versions = await _unitOfWork.NotificationTemplateVersionRepository.ListAsync(key, Channel, MaxVersionsListed, ct);
        IReadOnlyList<EmailTemplateVersionDto> items = versions
            .Select(v => new EmailTemplateVersionDto(
                v.Version, v.Action, v.RestoredFromVersion, v.Subject, v.Heading, v.BodyTemplate, v.Note, v.CreatedBy, v.CreatedAt))
            .ToList();
        return Result.Success(items);
    }

    public Result<EmailTemplatePreviewDto> Preview(string key, EmailTemplateDraftRequest draft)
    {
        var definition = EmailTemplateCatalog.Find(key);
        if (definition is null) return UnknownTemplate<EmailTemplatePreviewDto>(key);

        var content = Content(draft.Subject, draft.Heading, draft.BodyHtml);
        var issues = EmailTemplateRenderer.Validate(definition, content);
        // Rendered even when invalid: an admin fixing a typo needs to see the email they are fixing.
        var rendered = EmailTemplateRenderer.Render(definition, content, EmailTemplateCatalog.SampleValues(definition));
        return Result.Success(new EmailTemplatePreviewDto(rendered.Subject, rendered.HtmlBody, rendered.TextBody, issues));
    }

    public async Task<Result<EmailTemplateTestSendDto>> SendTestAsync(
        string key, EmailTemplateDraftRequest draft, string? adminEmail, CancellationToken ct = default)
    {
        var definition = EmailTemplateCatalog.Find(key);
        if (definition is null) return UnknownTemplate<EmailTemplateTestSendDto>(key);

        if (string.IsNullOrWhiteSpace(adminEmail))
            return Result.Failure<EmailTemplateTestSendDto>(
                "Your session carries no email address to send the test to.", ErrorCodes.ValidationError);
        if (_emailSender is null)
            return Result.Failure<EmailTemplateTestSendDto>(
                "Email sending is not configured on this deployment.", ErrorCodes.ServiceUnavailable);

        var content = Content(draft.Subject, draft.Heading, draft.BodyHtml);
        var issues = EmailTemplateRenderer.Validate(definition, content);
        if (issues.Count > 0)
            return Result.Failure<EmailTemplateTestSendDto>(issues[0].Message, ErrorCodes.ValidationError);

        var rendered = EmailTemplateRenderer.Render(definition, content, EmailTemplateCatalog.SampleValues(definition));
        var subject = $"[Test] {rendered.Subject}";
        var delivered = await _emailSender.SendEmailAsync(
            new EmailMessage(adminEmail, subject, rendered.HtmlBody, TextBody: rendered.TextBody), ct);
        if (!delivered)
            return Result.Failure<EmailTemplateTestSendDto>(
                "The email provider did not accept the test email. Try again in a moment.", ErrorCodes.ServiceUnavailable);

        _logger.LogInformation("Test email for template {TemplateKey} sent to the requesting admin.", key);
        return Result.Success(new EmailTemplateTestSendDto(adminEmail, subject));
    }

    private async Task<Result<EmailTemplateDetailDto>> WriteAsync(
        Guid adminId,
        EmailTemplateDefinition definition,
        EmailTemplateContent content,
        string action,
        int? restoredFrom,
        string? note,
        int? expectedVersion,
        CancellationToken ct)
    {
        var issues = EmailTemplateRenderer.Validate(definition, content);
        if (issues.Count > 0)
            return Result.Failure<EmailTemplateDetailDto>(issues[0].Message, ErrorCodes.ValidationError);

        var repository = _unitOfWork.NotificationTemplateRepository;
        var row = await repository.GetByTypeAsync(definition.Key, Channel, ct);
        var currentVersion = row?.Version ?? 0;
        if (expectedVersion is { } expected && expected != currentVersion)
        {
            return Result.Failure<EmailTemplateDetailDto>(
                $"Someone saved this email since you opened it (now version {currentVersion}). Reload to see their change before saving yours.",
                ErrorCodes.Conflict);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (row is null)
        {
            row = new NotificationTemplate
            {
                Id = Guid.CreateVersion7(),
                Type = definition.Key,
                Channel = Channel,
                CreatedAt = now,
                CreatedBy = adminId,
            };
            await repository.AddAsync(row);
        }

        row.Subject = content.Subject;
        row.Heading = content.Heading;
        row.BodyTemplate = content.BodyHtml;
        row.Variables = JsonSerializer.Serialize(definition.Variables.Select(variable => variable.Name));
        row.IsActive = true;
        row.Version = currentVersion + 1;
        row.UpdatedAt = now;
        row.UpdatedBy = adminId;

        await _unitOfWork.NotificationTemplateVersionRepository.AddAsync(
            VersionEntry(definition.Key, row.Version, action, restoredFrom, content, note, adminId, now), ct);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation(
            "Email template {TemplateKey} {Action} by {AdminId} (v{Version}).", definition.Key, action, adminId, row.Version);
        return Result.Success(Detail(definition, row));
    }

    private static EmailTemplateContent Content(string? subject, string? heading, string? bodyHtml) =>
        new((subject ?? string.Empty).Trim(), (heading ?? string.Empty).Trim(), bodyHtml ?? string.Empty);

    private static NotificationTemplateVersion VersionEntry(
        string key, int version, string action, int? restoredFrom, EmailTemplateContent content, string? note, Guid adminId, DateTime now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TemplateType = key,
            Channel = Channel,
            Version = version,
            Action = action,
            RestoredFromVersion = restoredFrom,
            Subject = content.Subject,
            Heading = content.Heading,
            BodyTemplate = content.BodyHtml,
            Note = note,
            CreatedBy = adminId,
            CreatedAt = now,
        };

    private static bool IsCustomized(NotificationTemplate? row) => row is { IsActive: true };

    private static EmailTemplateContentDto CurrentContent(EmailTemplateDefinition definition, NotificationTemplate? row) =>
        IsCustomized(row)
            ? new EmailTemplateContentDto(row!.Subject ?? string.Empty, row.Heading ?? string.Empty, row.BodyTemplate)
            : ToDto(definition.Default);

    private static EmailTemplateContentDto ToDto(EmailTemplateContent content) =>
        new(content.Subject, content.Heading, content.BodyHtml);

    private static EmailTemplateSummaryDto Summary(EmailTemplateDefinition definition, NotificationTemplate? row) =>
        new(
            definition.Key,
            definition.Name,
            definition.Description,
            definition.Service,
            definition.Provider,
            definition.Trigger,
            definition.IsLive,
            definition.DormantReason,
            definition.Variables.Select(v => new EmailTemplateVariableDto(v.Name, v.Description, v.Sample, v.Required)).ToList(),
            IsCustomized(row),
            row?.Version ?? 0,
            CurrentContent(definition, row).Subject,
            row?.UpdatedAt,
            row?.UpdatedBy);

    private static EmailTemplateDetailDto Detail(EmailTemplateDefinition definition, NotificationTemplate? row) =>
        new(Summary(definition, row), CurrentContent(definition, row), ToDto(definition.Default));

    private static Result<T> UnknownTemplate<T>(string key) =>
        Result.Failure<T>($"The platform sends no email called '{key}'.", ErrorCodes.NotFound);
}
