using WarpTalk.Shared.Email;

namespace WarpTalk.NotificationService.Application.DTOs.EmailTemplates;

public sealed record EmailTemplateVariableDto(string Name, string Description, string Sample, bool Required);

public sealed record EmailTemplateContentDto(string Subject, string Heading, string BodyHtml);

/// <summary>One card on the admin list.</summary>
public sealed record EmailTemplateSummaryDto(
    string Key,
    string Name,
    string Description,
    string Service,
    // Resend or SMTP: the two send paths, both of which read the stored template.
    string Provider,
    string Trigger,
    bool IsLive,
    string? DormantReason,
    IReadOnlyList<EmailTemplateVariableDto> Variables,
    // True when an admin-saved version is what senders use now.
    bool IsCustomized,
    // 0 when nobody has ever saved one.
    int Version,
    // The subject senders use now, placeholders unfilled.
    string Subject,
    DateTime? UpdatedAt,
    Guid? UpdatedBy);

public sealed record EmailTemplateDetailDto(
    EmailTemplateSummaryDto Template,
    // What senders use now: the saved version, or the default when not customised.
    EmailTemplateContentDto Current,
    EmailTemplateContentDto Default);

/// <summary>PUT body. <see cref="ExpectedVersion"/> refuses a save over someone else's newer one.</summary>
public sealed record SaveEmailTemplateRequest(
    string Subject,
    string Heading,
    string BodyHtml,
    int? ExpectedVersion = null,
    string? Note = null);

/// <summary>An unsaved edit, for the preview and the test email.</summary>
public sealed record EmailTemplateDraftRequest(string Subject, string Heading, string BodyHtml);

public sealed record EmailTemplatePreviewDto(
    string Subject,
    string Html,
    string Text,
    IReadOnlyList<EmailTemplateIssue> Issues);

public sealed record EmailTemplateTestSendDto(string SentTo, string Subject);

public sealed record EmailTemplateVersionDto(
    int Version,
    string Action,
    int? RestoredFromVersion,
    string Subject,
    string Heading,
    string BodyHtml,
    string? Note,
    Guid CreatedBy,
    DateTime CreatedAt);
