using System.Text.RegularExpressions;
using WarpTalk.NotificationService.Application.DTOs.EmailTemplates;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared;
using WarpTalk.Shared.Email;

namespace WarpTalk.NotificationService.Application.Services.EmailCms;

public interface IEmailCustomTemplateService
{
    Task<Result<EmailTemplateDetailDto>> CreateAsync(AdminActorContext actor, CreateCustomEmailTemplateRequest request, CancellationToken ct = default);
    Task<Result<EmailTemplateDetailDto>> UpdateAsync(AdminActorContext actor, string key, UpdateCustomEmailTemplateRequest request, CancellationToken ct = default);
    Task<Result<CustomEmailDeletionCheckDto>> CheckDeletionAsync(string key, CancellationToken ct = default);
    Task<Result> DeleteAsync(AdminActorContext actor, string key, DeleteCustomEmailTemplateRequest request, CancellationToken ct = default);
    Task<Result<EmailTemplateDetailDto>> RestoreAsync(AdminActorContext actor, string key, CancellationToken ct = default);
}

/// <summary>
/// Templates an admin creates. Built-in templates (EmailTemplateCatalog) cannot be created,
/// renamed or deleted here: each is bound to the code that sends it. A custom one has no code
/// sender, so its only routes out are an audience send and an announcement's email channel.
///
/// Deleting is soft (status DELETED, with a reason) and can be undone. A template that never
/// handed an email to the provider can be removed for good, with its drafts, versions and samples;
/// one that did cannot, because the send log and delivery counters would point at nothing.
/// </summary>
public sealed partial class EmailCustomTemplateService : IEmailCustomTemplateService
{
    public const int MinKeyLength = 3;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IEmailContentService _content;
    private readonly TimeProvider _time;

    public EmailCustomTemplateService(IUnitOfWork unitOfWork, IEmailContentService content, TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _content = content;
        _time = timeProvider ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,59}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,39}$")]
    private static partial Regex VariableNamePattern();

    public async Task<Result<EmailTemplateDetailDto>> CreateAsync(
        AdminActorContext actor, CreateCustomEmailTemplateRequest request, CancellationToken ct = default)
    {
        var key = (request.Key ?? string.Empty).Trim().ToLowerInvariant();
        if (!KeyPattern().IsMatch(key))
            return Invalid<EmailTemplateDetailDto>("The key must be 3–60 lower-case letters, digits, dots, dashes or underscores, starting with a letter or digit.");
        if (EmailTemplateCatalog.Find(key) is not null)
            return Result.Failure<EmailTemplateDetailDto>($"'{key}' is a built-in email. Choose another key.", ErrorCodes.Conflict);
        var repository = _unitOfWork.EmailCustomTemplateRepository;
        if (await repository.GetByKeyAsync(key, ct) is { } existing)
        {
            return Result.Failure<EmailTemplateDetailDto>(
                existing.Status == EmailCmsConstants.StatusDeleted
                    ? $"A deleted template already uses '{key}'. Restore it from Archived, or choose another key."
                    : $"A template called '{key}' already exists.",
                ErrorCodes.Conflict);
        }

        var details = ValidateDetails(request.Name, request.Description, request.Category, request.Variables);
        if (details.Error is not null) return Invalid<EmailTemplateDetailDto>(details.Error);

        var content = (request.Content ?? []).ToList();
        if (!content.Any(c => EmailLocales.Normalize(c.Locale) == EmailLocales.Default))
            return Invalid<EmailTemplateDetailDto>("Write the English version: it is what every other language falls back to.");
        if (content.Select(c => EmailLocales.Normalize(c.Locale)).Any(l => l is null))
            return Invalid<EmailTemplateDetailDto>($"Locale must be one of {string.Join(", ", EmailLocales.Supported)}.");
        if (content.GroupBy(c => EmailLocales.Normalize(c.Locale)).Any(g => g.Count() > 1))
            return Invalid<EmailTemplateDetailDto>("Each language can be written once.");

        if (request.LayoutId is { } layoutId)
        {
            var layout = await _unitOfWork.EmailBlockRepository.GetByIdAsync(layoutId, ct);
            if (layout is null || layout.Kind != EmailCmsConstants.KindLayout || layout.Status != EmailCmsConstants.StatusActive)
                return Invalid<EmailTemplateDetailDto>("That layout does not exist or is archived.");
        }

        var now = Now;
        var template = new EmailCustomTemplate
        {
            Id = Guid.CreateVersion7(),
            Key = key,
            Name = details.Name,
            Description = details.Description,
            Category = details.Category,
            Variables = CustomEmailDefinitions.WriteVariables(details.Variables),
            Status = EmailCmsConstants.StatusActive,
            CreatedBy = actor.ActorId,
            CreatedAt = now,
            UpdatedBy = actor.ActorId,
            UpdatedAt = now,
        };

        foreach (var locale in content)
        {
            var draft = EmailContentService.Normalize(locale.Subject, locale.Preheader, locale.Heading, locale.BodyHtml, locale.TextBody);
            var shape = EmailContentService.DraftShapeError(draft);
            if (shape is not null) return Invalid<EmailTemplateDetailDto>($"{locale.Locale}: {shape}");
            if (string.IsNullOrWhiteSpace(draft.Subject)) return Invalid<EmailTemplateDetailDto>($"{locale.Locale}: the subject is required.");
            if (string.IsNullOrWhiteSpace(draft.BodyHtml)) return Invalid<EmailTemplateDetailDto>($"{locale.Locale}: the body is required.");
        }

        await repository.AddAsync(template, ct);
        foreach (var locale in content)
        {
            var draft = EmailContentService.Normalize(locale.Subject, locale.Preheader, locale.Heading, locale.BodyHtml, locale.TextBody);
            var variant = EmailContentService.NewVariant(key, EmailLocales.Normalize(locale.Locale)!, actor.ActorId, now);
            EmailContentService.ApplyDraft(variant, draft, request.LayoutId, actor.ActorId, now);
            await _unitOfWork.EmailContentVariantRepository.AddAsync(variant, ct);
        }
        await _unitOfWork.SaveChangesAsync();

        return await _content.GetAsync(key, ct);
    }

    public async Task<Result<EmailTemplateDetailDto>> UpdateAsync(
        AdminActorContext actor, string key, UpdateCustomEmailTemplateRequest request, CancellationToken ct = default)
    {
        var (template, error) = await FindCustomAsync(key, ct);
        if (error is not null) return Result.Failure<EmailTemplateDetailDto>(error.Value.Message, error.Value.Code);
        if (template!.Status == EmailCmsConstants.StatusDeleted)
            return Result.Failure<EmailTemplateDetailDto>("This template is deleted. Restore it before changing it.", ErrorCodes.InvalidState);

        var details = ValidateDetails(request.Name, request.Description, request.Category, request.Variables);
        if (details.Error is not null) return Invalid<EmailTemplateDetailDto>(details.Error);

        template.Name = details.Name;
        template.Description = details.Description;
        template.Category = details.Category;
        template.Variables = CustomEmailDefinitions.WriteVariables(details.Variables);
        template.UpdatedBy = actor.ActorId;
        template.UpdatedAt = Now;
        await _unitOfWork.SaveChangesAsync();

        return await _content.GetAsync(template.Key, ct);
    }

    public async Task<Result<CustomEmailDeletionCheckDto>> CheckDeletionAsync(string key, CancellationToken ct = default)
    {
        var (template, error) = await FindCustomAsync(key, ct);
        if (error is not null) return Result.Failure<CustomEmailDeletionCheckDto>(error.Value.Message, error.Value.Code);
        return Result.Success(await DeletionFactsAsync(template!.Key, ct));
    }

    public async Task<Result> DeleteAsync(AdminActorContext actor, string key, DeleteCustomEmailTemplateRequest request, CancellationToken ct = default)
    {
        if (EmailTemplateCatalog.Find(key) is { } builtIn)
        {
            return Result.Failure(
                $"\"{builtIn.Name}\" is sent by the {builtIn.Service} service and cannot be deleted. Reset it to the default wording, or archive it so the sender uses the built-in text.",
                ErrorCodes.InvalidState);
        }
        var (template, error) = await FindCustomAsync(key, ct);
        if (error is not null) return Result.Failure(error.Value.Message, error.Value.Code);

        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length == 0) return Result.Failure("Say why it is being deleted — the audit log keeps the reason.", ErrorCodes.ValidationError);
        if (reason.Length > EmailCmsConstants.MaxDeleteReasonLength)
            return Result.Failure($"The reason must be {EmailCmsConstants.MaxDeleteReasonLength} characters or fewer.", ErrorCodes.ValidationError);

        if (await HasActiveSendAsync(template!.Key, ct))
            return Result.Failure("A send of this template is queued or in progress. Cancel it first.", ErrorCodes.InvalidState);

        if (request.Permanent)
        {
            var facts = await DeletionFactsAsync(template.Key, ct);
            if (!facts.CanDeletePermanently) return Result.Failure(facts.Reason ?? "It cannot be deleted for good.", ErrorCodes.InvalidState);

            foreach (var variant in await _unitOfWork.EmailContentVariantRepository.ListAsync(template.Key, ct))
            {
                var tracked = await _unitOfWork.EmailContentVariantRepository.GetAsync(template.Key, variant.Locale, ct);
                if (tracked is not null) _unitOfWork.EmailContentVariantRepository.Remove(tracked);
            }
            foreach (var set in await _unitOfWork.EmailSampleDataSetRepository.ListAsync(template.Key, ct))
            {
                var tracked = await _unitOfWork.EmailSampleDataSetRepository.GetByIdAsync(set.Id, ct);
                if (tracked is not null) _unitOfWork.EmailSampleDataSetRepository.Remove(tracked);
            }
            _unitOfWork.EmailCustomTemplateRepository.Remove(template);
            await _unitOfWork.SaveChangesAsync();
            return Result.Success();
        }

        if (template.Status == EmailCmsConstants.StatusDeleted)
            return Result.Failure("It is already deleted.", ErrorCodes.InvalidState);

        var now = Now;
        template.Status = EmailCmsConstants.StatusDeleted;
        template.DeletedAt = now;
        template.DeletedBy = actor.ActorId;
        template.DeleteReason = reason;
        template.UpdatedAt = now;
        template.UpdatedBy = actor.ActorId;
        await _unitOfWork.SaveChangesAsync();
        return Result.Success();
    }

    public async Task<Result<EmailTemplateDetailDto>> RestoreAsync(AdminActorContext actor, string key, CancellationToken ct = default)
    {
        var (template, error) = await FindCustomAsync(key, ct);
        if (error is not null) return Result.Failure<EmailTemplateDetailDto>(error.Value.Message, error.Value.Code);
        if (template!.Status != EmailCmsConstants.StatusDeleted)
            return Result.Failure<EmailTemplateDetailDto>("It is not deleted.", ErrorCodes.InvalidState);

        template.Status = EmailCmsConstants.StatusActive;
        template.DeletedAt = null;
        template.DeletedBy = null;
        template.DeleteReason = null;
        template.UpdatedAt = Now;
        template.UpdatedBy = actor.ActorId;
        await _unitOfWork.SaveChangesAsync();
        return await _content.GetAsync(template.Key, ct);
    }

    // ── Rules ─────────────────────────────────────────────────────────────────────────────

    private sealed record Details(
        string Name, string? Description, string Category, IReadOnlyList<EmailCustomVariable> Variables, string? Error);

    private static Details ValidateDetails(
        string? name, string? description, string? category, IReadOnlyList<CustomEmailVariableRequest>? variables)
    {
        Details Fail(string message) => new(string.Empty, null, string.Empty, [], message);

        var cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0) return Fail("Name the template.");
        if (cleanName.Length > EmailCmsConstants.MaxNameLength) return Fail($"The name must be {EmailCmsConstants.MaxNameLength} characters or fewer.");
        var cleanDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if ((cleanDescription?.Length ?? 0) > EmailCmsConstants.MaxDescriptionLength)
            return Fail($"The description must be {EmailCmsConstants.MaxDescriptionLength} characters or fewer.");
        var cleanCategory = (category ?? string.Empty).Trim().ToUpperInvariant();
        if (!EmailCmsConstants.Categories.Contains(cleanCategory))
            return Fail($"Category must be one of {string.Join(", ", EmailCmsConstants.Categories)}.");

        var list = new List<EmailCustomVariable>();
        foreach (var variable in variables ?? [])
        {
            var variableName = (variable.Name ?? string.Empty).Trim();
            if (!VariableNamePattern().IsMatch(variableName))
                return Fail($"'{variableName}' is not a valid variable name: letters, digits and _, starting with a letter.");
            if (CustomEmailDefinitions.IsImplicit(variableName))
                return Fail($"{{{{{variableName}}}}} is filled in automatically; it cannot be declared.");
            if (list.Any(v => string.Equals(v.Name, variableName, StringComparison.Ordinal)))
                return Fail($"{{{{{variableName}}}}} is declared twice.");
            var type = string.IsNullOrWhiteSpace(variable.Type) ? EmailCmsConstants.VariableText : variable.Type.Trim().ToUpperInvariant();
            if (!EmailCmsConstants.VariableTypes.Contains(type))
                return Fail($"The type of {{{{{variableName}}}}} must be one of {string.Join(", ", EmailCmsConstants.VariableTypes)}.");
            var sample = variable.Sample ?? string.Empty;
            if (sample.Length > EmailCmsConstants.MaxSampleValueLength) return Fail($"The sample value of {{{{{variableName}}}}} is too long.");
            if (type == EmailCmsConstants.VariableUrl && sample.Length > 0 && !Uri.TryCreate(sample, UriKind.Absolute, out _))
                return Fail($"The sample value of {{{{{variableName}}}}} must be a full link (https://…).");
            var label = string.IsNullOrWhiteSpace(variable.Label) ? variableName : variable.Label.Trim();
            if (label.Length > EmailCmsConstants.MaxNameLength) return Fail($"The label of {{{{{variableName}}}}} is too long.");
            list.Add(new EmailCustomVariable(variableName, label, type, sample, variable.Required));
        }
        if (list.Count > EmailCmsConstants.MaxCustomVariables)
            return Fail($"A template can declare {EmailCmsConstants.MaxCustomVariables} variables at most.");

        return new Details(cleanName, cleanDescription, cleanCategory, list, null);
    }

    private async Task<(EmailCustomTemplate? Template, (string Message, string Code)? Error)> FindCustomAsync(string key, CancellationToken ct)
    {
        if (EmailTemplateCatalog.Find(key) is not null)
            return (null, ("Built-in emails are defined by the service that sends them and cannot be changed here.", ErrorCodes.InvalidState));
        var template = await _unitOfWork.EmailCustomTemplateRepository.GetByKeyAsync(key, ct);
        return template is null ? (null, ($"There is no template called '{key}'.", ErrorCodes.NotFound)) : (template, null);
    }

    private async Task<bool> HasActiveSendAsync(string key, CancellationToken ct)
    {
        var campaigns = await _unitOfWork.EmailCampaignRepository.ListForTemplateAsync(key, 200, ct);
        return campaigns.Any(c => c.Status is EmailCmsConstants.CampaignQueued or EmailCmsConstants.CampaignSending);
    }

    private async Task<CustomEmailDeletionCheckDto> DeletionFactsAsync(string key, CancellationToken ct)
    {
        var campaigns = await _unitOfWork.EmailCampaignRepository.ListForTemplateAsync(key, 1000, ct);
        var stats = await _unitOfWork.EmailDeliveryStatRepository.ListSinceAsync(key, DateOnly.MinValue, ct);
        var sent = campaigns.Sum(c => c.SentCount) + stats.Sum(s => s.SentCount);
        var everSent = sent > 0 || await _unitOfWork.EmailCampaignRepository.AnySentAsync(key, ct);
        return new CustomEmailDeletionCheckDto(
            CanDeletePermanently: !everSent && campaigns.Count == 0,
            Reason: everSent
                ? "It has been sent, so its send log must keep pointing at it. It can be deleted (and restored), not removed for good."
                : campaigns.Count > 0 ? "It has sends on record. Delete it instead; it can be restored." : null,
            CampaignCount: campaigns.Count,
            SentCount: sent);
    }

    private static Result<T> Invalid<T>(string message) => Result.Failure<T>(message, ErrorCodes.ValidationError);
}
