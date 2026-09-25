using System.Text.Json;
using System.Text.Json.Serialization;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared.Email;

namespace WarpTalk.NotificationService.Application.Services.EmailCms;

/// <summary>A variable an admin declared on a custom template.</summary>
/// <param name="Type">TEXT, URL, DATE, NUMBER or MULTILINE — how the send form asks for it.</param>
public sealed record EmailCustomVariable(string Name, string Label, string Type, string Sample, bool Required);

/// <summary>One email the CMS manages: its definition, and the custom row when an admin made it.</summary>
public sealed record EmailDefinitionEntry(EmailTemplateDefinition Definition, EmailCustomTemplate? Custom)
{
    public bool IsCustom => Custom is not null;

    public bool IsDeleted => Custom?.Status == EmailCmsConstants.StatusDeleted;
}

/// <summary>
/// Every email the CMS can edit: the code catalog (bound to the services that send them) plus the
/// templates admins created. The one place that answers "is this a real email, and what can it say".
/// </summary>
public interface IEmailDefinitionProvider
{
    Task<EmailDefinitionEntry?> FindAsync(string key, bool includeDeleted = false, CancellationToken ct = default);

    Task<IReadOnlyList<EmailDefinitionEntry>> ListAsync(bool includeDeleted, CancellationToken ct = default);
}

public sealed class EmailDefinitionProvider : IEmailDefinitionProvider
{
    private readonly IUnitOfWork _unitOfWork;

    public EmailDefinitionProvider(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<EmailDefinitionEntry?> FindAsync(string key, bool includeDeleted = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var builtIn = EmailTemplateCatalog.Find(key);
        if (builtIn is not null) return new EmailDefinitionEntry(builtIn, null);

        var custom = await _unitOfWork.EmailCustomTemplateRepository.GetByKeyAsync(key, ct);
        if (custom is null) return null;
        if (!includeDeleted && custom.Status == EmailCmsConstants.StatusDeleted) return null;
        return new EmailDefinitionEntry(CustomEmailDefinitions.ToDefinition(custom), custom);
    }

    public async Task<IReadOnlyList<EmailDefinitionEntry>> ListAsync(bool includeDeleted, CancellationToken ct = default)
    {
        var custom = await _unitOfWork.EmailCustomTemplateRepository.ListAsync(includeDeleted, ct);
        return EmailTemplateCatalog.All
            .Select(definition => new EmailDefinitionEntry(definition, null))
            .Concat(custom.Select(row => new EmailDefinitionEntry(CustomEmailDefinitions.ToDefinition(row), row)))
            .ToList();
    }
}

/// <summary>How a custom template becomes an <see cref="EmailTemplateDefinition"/> the renderer understands.</summary>
public static class CustomEmailDefinitions
{
    public const string Service = "notification";
    public const string Provider = "Resend";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static EmailTemplateDefinition ToDefinition(EmailCustomTemplate template)
    {
        var variables = ImplicitVariables(template.Category)
            .Concat(ReadVariables(template.Variables).Select(ToTemplateVariable))
            .ToList();
        return new EmailTemplateDefinition(
            template.Key,
            template.Name,
            template.Description ?? string.Empty,
            Service,
            Provider,
            TriggerFor(template.Category),
            IsLive: template.Status == EmailCmsConstants.StatusActive,
            DormantReason: template.Status == EmailCmsConstants.StatusDeleted ? "Deleted — restore it to send it again." : null,
            variables,
            StarterContent(template.Name));
    }

    /// <summary>What a new locale of a custom template starts from.</summary>
    public static EmailTemplateContent StarterContent(string name) =>
        new(
            name,
            name,
            $"<p>Hi {{{{{EmailCmsConstants.VariableRecipientName}}}}},</p>\n<p></p>",
            string.Empty);

    public static string TriggerFor(string category) => category switch
    {
        EmailCmsConstants.CategoryAnnouncement => "Sent with an announcement, or by an admin to an audience.",
        EmailCmsConstants.CategoryMarketing => "Sent by an admin to an audience. People who turned off promotional email are skipped.",
        _ => "Sent by an admin to an audience.",
    };

    /// <summary>Filled in by the sender, so every custom template may use them.</summary>
    public static IReadOnlyList<EmailTemplateVariable> ImplicitVariables(string category)
    {
        var variables = new List<EmailTemplateVariable>
        {
            new(EmailCmsConstants.VariableRecipientName, "The recipient's name (filled in for each person).", "Linh Nguyen"),
            new(EmailCmsConstants.VariableRecipientEmail, "The recipient's email address (filled in for each person).", "linh@example.com"),
        };
        if (category == EmailCmsConstants.CategoryAnnouncement)
        {
            variables.Add(new(EmailCmsConstants.VariableAnnouncementTitle, "The announcement's title.", "Live captions now in 40 languages"));
            variables.Add(new(EmailCmsConstants.VariableAnnouncementText, "The announcement's text, as plain text.", "Turn them on from any meeting's caption menu.", Multiline: true));
            variables.Add(new(EmailCmsConstants.VariableAnnouncementLink, "The announcement's button link.", "https://app.warptalk.vn/"));
        }
        return variables;
    }

    public static bool IsImplicit(string name) =>
        name is EmailCmsConstants.VariableRecipientName
            or EmailCmsConstants.VariableRecipientEmail
            or EmailCmsConstants.VariableAnnouncementTitle
            or EmailCmsConstants.VariableAnnouncementText
            or EmailCmsConstants.VariableAnnouncementLink;

    public static IReadOnlyList<EmailCustomVariable> ReadVariables(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<EmailCustomVariable>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string WriteVariables(IReadOnlyList<EmailCustomVariable> variables) =>
        JsonSerializer.Serialize(variables, Json);

    /// <remarks>
    /// A custom variable's "required" means the send form must be given a value, not that every
    /// language's content must mention it — so it is not the renderer's Required (which refuses to
    /// publish content that leaves it out). The send checks it (EmailCampaignService).
    /// </remarks>
    private static EmailTemplateVariable ToTemplateVariable(EmailCustomVariable variable) =>
        new(
            variable.Name,
            string.IsNullOrWhiteSpace(variable.Label) ? variable.Name : variable.Label,
            variable.Sample,
            Required: false,
            Multiline: variable.Type == EmailCmsConstants.VariableMultiline);
}
