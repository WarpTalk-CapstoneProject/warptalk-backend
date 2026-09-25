using System.Net.Mail;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WarpTalk.NotificationService.Application.DTOs.Common;
using WarpTalk.NotificationService.Application.DTOs.EmailTemplates;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Email;

namespace WarpTalk.NotificationService.Application.Services.EmailCms;

public interface IEmailContentService
{
    Task<Result<IReadOnlyList<EmailTemplateListItemDto>>> ListAsync(CancellationToken ct = default);
    Task<Result<EmailTemplateDetailDto>> GetAsync(string key, CancellationToken ct = default);
    Task<Result<EmailVariantDto>> SaveDraftAsync(AdminActorContext actor, string key, string locale, SaveEmailDraftRequest request, CancellationToken ct = default);
    Task<Result<EmailVariantDto>> PublishAsync(AdminActorContext actor, string key, string locale, PublishEmailRequest request, CancellationToken ct = default);
    Task<Result<EmailVariantDto?>> DiscardDraftAsync(AdminActorContext actor, string key, string locale, CancellationToken ct = default);
    Task<Result<EmailVariantDto>> ArchiveAsync(AdminActorContext actor, string key, string locale, CancellationToken ct = default);
    Task<Result<EmailVariantDto>> UnarchiveAsync(AdminActorContext actor, string key, string locale, CancellationToken ct = default);
    Task<Result<EmailVariantDto>> DuplicateAsync(AdminActorContext actor, string key, string locale, DuplicateEmailVariantRequest request, CancellationToken ct = default);
    Task<Result<EmailVariantDto>> ResetToDefaultAsync(AdminActorContext actor, string key, string locale, CancellationToken ct = default);
    Task<Result<IReadOnlyList<EmailCmsVersionDto>>> ListVersionsAsync(string key, string locale, CancellationToken ct = default);
    Task<Result<EmailVariantDto>> RestoreVersionAsync(AdminActorContext actor, string key, string locale, int version, CancellationToken ct = default);
    Task<Result<EmailPreviewDto>> PreviewAsync(string key, EmailPreviewRequest request, CancellationToken ct = default);
    Task<Result<EmailRenderedDto>> RenderAsync(string key, EmailRenderQuery query, CancellationToken ct = default);
    Task<Result<EmailTestSendDto>> SendTestAsync(AdminActorContext actor, string key, EmailTestSendRequest request, CancellationToken ct = default);
    Task<Result<EmailSampleDataSetDto>> SaveSampleSetAsync(AdminActorContext actor, string key, Guid? id, SaveSampleDataSetRequest request, CancellationToken ct = default);
    Task<Result> DeleteSampleSetAsync(AdminActorContext actor, string key, Guid id, CancellationToken ct = default);
    Task<Result<EmailStatsDto>> GetStatsAsync(string key, int days, CancellationToken ct = default);
    Task<Result<BulkResultDto>> BulkAsync(AdminActorContext actor, EmailBulkRequest request, CancellationToken ct = default);
}

/// <summary>
/// The email content CMS: one row per email per locale, with a draft side that only the admin
/// sees and a published side that senders read.
///
/// The emails themselves are <see cref="EmailTemplateCatalog"/> — a row can only exist for an
/// email a sender composes, and every such email is listed whether or not anyone has written a
/// variant. Nothing here can make a sender send a draft: publishing is the only write that
/// touches the published columns, and it validates against the catalog first.
/// </summary>
public sealed class EmailContentService : IEmailContentService
{
    private const int MaxVersionsListed = 50;
    private const int StatsWindowDays = 30;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IEmailDefinitionProvider _definitions;
    private readonly EmailEnvelope _envelope;
    private readonly IEmailSender? _emailSender;
    private readonly TimeProvider _time;
    private readonly ILogger<EmailContentService> _logger;

    public EmailContentService(
        IUnitOfWork unitOfWork,
        ILogger<EmailContentService> logger,
        IEmailSender? emailSender = null,
        TimeProvider? timeProvider = null,
        IEmailDefinitionProvider? definitions = null,
        EmailEnvelope? envelope = null)
    {
        _envelope = envelope ?? EmailEnvelope.Default;
        _unitOfWork = unitOfWork;
        _definitions = definitions ?? new EmailDefinitionProvider(unitOfWork);
        _logger = logger;
        _emailSender = emailSender;
        _time = timeProvider ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ── Reads ──────────────────────────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<EmailTemplateListItemDto>>> ListAsync(CancellationToken ct = default)
    {
        var variants = await _unitOfWork.EmailContentVariantRepository.ListAsync(null, ct);
        var blocks = await _unitOfWork.EmailBlockRepository.ListAsync(EmailCmsConstants.KindLayout, ct);
        var stats = await _unitOfWork.EmailDeliveryStatRepository.ListSinceAsync(null, StatsSince(StatsWindowDays), ct);

        var entries = await _definitions.ListAsync(includeDeleted: true, ct);
        IReadOnlyList<EmailTemplateListItemDto> items = entries
            .Select(entry => ListItem(
                entry,
                variants.Where(v => v.TemplateKey == entry.Definition.Key).ToList(),
                blocks,
                stats.Where(s => s.TemplateKey == entry.Definition.Key)))
            .ToList();
        return Result.Success(items);
    }

    public async Task<Result<EmailTemplateDetailDto>> GetAsync(string key, CancellationToken ct = default)
    {
        var entry = await _definitions.FindAsync(key, includeDeleted: true, ct);
        if (entry is null) return UnknownTemplate<EmailTemplateDetailDto>(key);
        var definition = entry.Definition;

        var variants = await _unitOfWork.EmailContentVariantRepository.ListAsync(key, ct);
        var blocks = await _unitOfWork.EmailBlockRepository.ListAsync(EmailCmsConstants.KindLayout, ct);
        var stats = await _unitOfWork.EmailDeliveryStatRepository.ListSinceAsync(key, StatsSince(StatsWindowDays), ct);
        var sets = await _unitOfWork.EmailSampleDataSetRepository.ListAsync(key, ct);

        var sampleSets = new List<EmailSampleDataSetDto>
        {
            new(Guid.Empty, "Default sample", EmailTemplateCatalog.SampleValues(definition), BuiltIn: true, UpdatedAt: null),
        };
        sampleSets.AddRange(sets.Select(ToSampleDto));

        return Result.Success(new EmailTemplateDetailDto(
            ListItem(entry, variants, blocks, stats),
            FromDefault(definition.Default),
            variants.OrderBy(v => Array.IndexOf(EmailLocales.Supported, v.Locale)).Select(ToVariantDto).ToList(),
            sampleSets,
            EmailLocales.Supported));
    }

    public async Task<Result<IReadOnlyList<EmailCmsVersionDto>>> ListVersionsAsync(string key, string locale, CancellationToken ct = default)
    {
        var (definition, normalized, error) = await ResolveAsync(key, locale, ct);
        if (error is not null) return Result.Failure<IReadOnlyList<EmailCmsVersionDto>>(error.Value.Message, error.Value.Code);

        var variant = await _unitOfWork.EmailContentVariantRepository.GetAsync(definition!.Key, normalized!, ct);
        if (variant is null) return Result.Success<IReadOnlyList<EmailCmsVersionDto>>([]);

        var versions = await _unitOfWork.EmailCmsVersionRepository.ListAsync(EmailCmsConstants.OwnerContent, variant.Id, MaxVersionsListed, ct);
        IReadOnlyList<EmailCmsVersionDto> items = versions.Select(ToVersionDto).ToList();
        return Result.Success(items);
    }

    public async Task<Result<EmailStatsDto>> GetStatsAsync(string key, int days, CancellationToken ct = default)
    {
        if (await _definitions.FindAsync(key, includeDeleted: true, ct) is null) return UnknownTemplate<EmailStatsDto>(key);
        days = Math.Clamp(days, 1, 365);

        var since = StatsSince(days);
        var rows = await _unitOfWork.EmailDeliveryStatRepository.ListSinceAsync(key, since, ct);
        var daily = Enumerable.Range(0, days)
            .Select(offset => since.AddDays(offset))
            .Select(day => new EmailStatsDayDto(
                day,
                rows.Where(r => r.Day == day).Sum(r => r.SentCount),
                rows.Where(r => r.Day == day).Sum(r => r.FailedCount)))
            .ToList();
        var byLocale = rows
            .GroupBy(r => r.Locale)
            .Select(group => new EmailStatsLocaleDto(group.Key, group.Sum(r => r.SentCount), group.Sum(r => r.FailedCount)))
            .OrderBy(l => Array.IndexOf(EmailLocales.Supported, l.Locale))
            .ToList();
        return Result.Success(new EmailStatsDto(
            days,
            new EmailDeliveryTotalsDto(rows.Sum(r => r.SentCount), rows.Sum(r => r.FailedCount)),
            daily,
            byLocale));
    }

    // ── Draft writes ───────────────────────────────────────────────────────────────────────

    public async Task<Result<EmailVariantDto>> SaveDraftAsync(
        AdminActorContext actor, string key, string locale, SaveEmailDraftRequest request, CancellationToken ct = default)
    {
        var (definition, normalized, error) = await ResolveAsync(key, locale, ct);
        if (error is not null) return Result.Failure<EmailVariantDto>(error.Value.Message, error.Value.Code);

        var draft = Normalize(request.Subject, request.Preheader, request.Heading, request.BodyHtml, request.TextBody);
        var shapeError = DraftShapeError(draft);
        if (shapeError is not null) return Result.Failure<EmailVariantDto>(shapeError, ErrorCodes.ValidationError);

        var layoutError = await LayoutChoiceErrorAsync(request.LayoutId, ct);
        if (layoutError is not null) return Result.Failure<EmailVariantDto>(layoutError, ErrorCodes.ValidationError);

        var repository = _unitOfWork.EmailContentVariantRepository;
        var variant = await repository.GetAsync(definition!.Key, normalized!, ct);
        if (variant is not null && request.ExpectedDraftUpdatedAt is { } expected && !SameInstant(expected, variant.DraftUpdatedAt))
        {
            return Result.Failure<EmailVariantDto>(
                "Someone saved this draft since you opened it. Reload to see their change before saving yours.",
                ErrorCodes.Conflict);
        }
        if (variant is { Status: EmailCmsConstants.StatusArchived })
            return Result.Failure<EmailVariantDto>("This locale is archived. Restore it before editing.", ErrorCodes.InvalidState);

        var now = Now;
        if (variant is null)
        {
            variant = NewVariant(definition.Key, normalized!, actor.ActorId, now);
            await repository.AddAsync(variant, ct);
        }

        ApplyDraft(variant, draft, request.LayoutId, actor.ActorId, now);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(ToVariantDto(variant));
    }

    public async Task<Result<EmailVariantDto?>> DiscardDraftAsync(AdminActorContext actor, string key, string locale, CancellationToken ct = default)
    {
        var (definition, normalized, error) = await ResolveAsync(key, locale, ct);
        if (error is not null) return Result.Failure<EmailVariantDto?>(error.Value.Message, error.Value.Code);

        var repository = _unitOfWork.EmailContentVariantRepository;
        var variant = await repository.GetAsync(definition!.Key, normalized!, ct);
        if (variant is null) return Result.Success<EmailVariantDto?>(null);

        EmailVariantDto? result;
        if (variant.PublishedVersion == 0)
        {
            // Never published: the draft is all there is, so discarding it removes the locale.
            repository.Remove(variant);
            result = null;
        }
        else
        {
            variant.DraftSubject = variant.PublishedSubject ?? string.Empty;
            variant.DraftPreheader = variant.PublishedPreheader ?? string.Empty;
            variant.DraftHeading = variant.PublishedHeading ?? string.Empty;
            variant.DraftBodyHtml = variant.PublishedBodyHtml ?? string.Empty;
            variant.DraftTextBody = variant.PublishedTextBody;
            variant.DraftLayoutId = variant.PublishedLayoutId;
            variant.DraftUpdatedAt = Now;
            variant.DraftUpdatedBy = actor.ActorId;
            result = ToVariantDto(variant);
        }
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(result);
    }

    public async Task<Result<EmailVariantDto>> ResetToDefaultAsync(AdminActorContext actor, string key, string locale, CancellationToken ct = default)
    {
        var (definition, normalized, error) = await ResolveAsync(key, locale, ct);
        if (error is not null) return Result.Failure<EmailVariantDto>(error.Value.Message, error.Value.Code);
        if (EmailTemplateCatalog.Find(key) is null)
            return Result.Failure<EmailVariantDto>(
                "A template you created has no built-in wording to go back to. Restore an earlier version from its history instead.",
                ErrorCodes.InvalidState);

        var repository = _unitOfWork.EmailContentVariantRepository;
        var variant = await repository.GetAsync(definition!.Key, normalized!, ct);
        var now = Now;
        if (variant is null)
        {
            variant = NewVariant(definition.Key, normalized!, actor.ActorId, now);
            await repository.AddAsync(variant, ct);
        }
        if (variant.Status == EmailCmsConstants.StatusArchived)
            return Result.Failure<EmailVariantDto>("This locale is archived. Restore it before editing.", ErrorCodes.InvalidState);

        // The draft only: it goes out when published, like any other edit.
        var fallback = definition.Default;
        ApplyDraft(variant, new EmailTemplateContent(fallback.Subject, fallback.Heading, fallback.BodyHtml, fallback.Preheader, null), null, actor.ActorId, now);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(ToVariantDto(variant));
    }

    public async Task<Result<EmailVariantDto>> RestoreVersionAsync(
        AdminActorContext actor, string key, string locale, int version, CancellationToken ct = default)
    {
        var (definition, normalized, error) = await ResolveAsync(key, locale, ct);
        if (error is not null) return Result.Failure<EmailVariantDto>(error.Value.Message, error.Value.Code);

        var variant = await _unitOfWork.EmailContentVariantRepository.GetAsync(definition!.Key, normalized!, ct);
        if (variant is null) return NotFound<EmailVariantDto>("This locale has no versions.");

        var entry = await _unitOfWork.EmailCmsVersionRepository.GetAsync(EmailCmsConstants.OwnerContent, variant.Id, version, ct);
        if (entry is null) return NotFound<EmailVariantDto>($"Version {version} does not exist.");

        var fields = ReadSnapshot(entry.Snapshot);
        Guid? layoutId = Guid.TryParse(fields.GetValueOrDefault("layoutId"), out var parsed) ? parsed : null;
        if (layoutId is { } id && await LayoutChoiceErrorAsync(id, ct) is not null) layoutId = null;

        ApplyDraft(variant, new EmailTemplateContent(
                fields.GetValueOrDefault("subject") ?? string.Empty,
                fields.GetValueOrDefault("heading") ?? string.Empty,
                fields.GetValueOrDefault("bodyHtml") ?? string.Empty,
                fields.GetValueOrDefault("preheader") ?? string.Empty,
                fields.GetValueOrDefault("textBody")),
            layoutId, actor.ActorId, Now);
        if (variant.Status == EmailCmsConstants.StatusArchived)
        {
            variant.Status = EmailCmsConstants.StatusActive;
            variant.ArchivedAt = null;
        }
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(ToVariantDto(variant));
    }

    // ── Publishing and lifecycle ──────────────────────────────────────────────────────────

    public async Task<Result<EmailVariantDto>> PublishAsync(
        AdminActorContext actor, string key, string locale, PublishEmailRequest request, CancellationToken ct = default)
    {
        var (definition, normalized, error) = await ResolveAsync(key, locale, ct);
        if (error is not null) return Result.Failure<EmailVariantDto>(error.Value.Message, error.Value.Code);

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > EmailCmsConstants.MaxNoteLength })
            return Result.Failure<EmailVariantDto>($"The note must be {EmailCmsConstants.MaxNoteLength} characters or fewer.", ErrorCodes.ValidationError);

        var variant = await _unitOfWork.EmailContentVariantRepository.GetAsync(definition!.Key, normalized!, ct);
        if (variant is null) return NotFound<EmailVariantDto>("There is no draft to publish in this locale.");
        if (variant.Status == EmailCmsConstants.StatusArchived)
            return Result.Failure<EmailVariantDto>("This locale is archived. Restore it before publishing.", ErrorCodes.InvalidState);
        if (request.ExpectedPublishedVersion is { } expected && expected != variant.PublishedVersion)
        {
            return Result.Failure<EmailVariantDto>(
                $"Someone published this email since you opened it (now v{variant.PublishedVersion}). Reload before publishing.",
                ErrorCodes.Conflict);
        }

        var publishError = await PublishErrorAsync(definition, variant, ct);
        if (publishError is not null) return Result.Failure<EmailVariantDto>(publishError, ErrorCodes.ValidationError);

        await PublishVariantAsync(variant, actor.ActorId, note, ct);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Email {TemplateKey} ({Locale}) published v{Version}.", definition.Key, normalized, variant.PublishedVersion);
        return Result.Success(ToVariantDto(variant));
    }

    public async Task<Result<EmailVariantDto>> ArchiveAsync(AdminActorContext actor, string key, string locale, CancellationToken ct = default)
    {
        var (definition, normalized, error) = await ResolveAsync(key, locale, ct);
        if (error is not null) return Result.Failure<EmailVariantDto>(error.Value.Message, error.Value.Code);

        var variant = await _unitOfWork.EmailContentVariantRepository.GetAsync(definition!.Key, normalized!, ct);
        if (variant is null) return NotFound<EmailVariantDto>("This locale has no content to archive.");
        if (variant.Status == EmailCmsConstants.StatusArchived)
            return Result.Failure<EmailVariantDto>("It is already archived.", ErrorCodes.InvalidState);

        variant.Status = EmailCmsConstants.StatusArchived;
        variant.ArchivedAt = Now;
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(ToVariantDto(variant));
    }

    public async Task<Result<EmailVariantDto>> UnarchiveAsync(AdminActorContext actor, string key, string locale, CancellationToken ct = default)
    {
        var (definition, normalized, error) = await ResolveAsync(key, locale, ct);
        if (error is not null) return Result.Failure<EmailVariantDto>(error.Value.Message, error.Value.Code);

        var variant = await _unitOfWork.EmailContentVariantRepository.GetAsync(definition!.Key, normalized!, ct);
        if (variant is null) return NotFound<EmailVariantDto>("This locale has no content.");
        if (variant.Status != EmailCmsConstants.StatusArchived)
            return Result.Failure<EmailVariantDto>("It is not archived.", ErrorCodes.InvalidState);

        variant.Status = EmailCmsConstants.StatusActive;
        variant.ArchivedAt = null;
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(ToVariantDto(variant));
    }

    public async Task<Result<EmailVariantDto>> DuplicateAsync(
        AdminActorContext actor, string key, string locale, DuplicateEmailVariantRequest request, CancellationToken ct = default)
    {
        var (definition, normalized, error) = await ResolveAsync(key, locale, ct);
        if (error is not null) return Result.Failure<EmailVariantDto>(error.Value.Message, error.Value.Code);
        var target = EmailLocales.Normalize(request.TargetLocale);
        if (target is null)
            return Result.Failure<EmailVariantDto>($"Locale must be one of {string.Join(", ", EmailLocales.Supported)}.", ErrorCodes.ValidationError);
        if (target == normalized)
            return Result.Failure<EmailVariantDto>("Choose a different locale to copy into.", ErrorCodes.ValidationError);

        var repository = _unitOfWork.EmailContentVariantRepository;
        var source = await repository.GetAsync(definition!.Key, normalized!, ct);
        var content = source is null
            ? definition.Default
            : new EmailTemplateContent(source.DraftSubject, source.DraftHeading, source.DraftBodyHtml, source.DraftPreheader, source.DraftTextBody);

        var existing = await repository.GetAsync(definition.Key, target, ct);
        if (existing is not null && !request.Overwrite)
        {
            return Result.Failure<EmailVariantDto>(
                $"The {target} version already exists. Copy with overwrite to replace its draft.", ErrorCodes.Conflict);
        }

        var now = Now;
        var copy = existing ?? NewVariant(definition.Key, target, actor.ActorId, now);
        if (existing is null) await repository.AddAsync(copy, ct);
        copy.Status = EmailCmsConstants.StatusActive;
        copy.ArchivedAt = null;
        ApplyDraft(copy, content, source?.DraftLayoutId, actor.ActorId, now);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(ToVariantDto(copy));
    }

    // ── Preview and test ─────────────────────────────────────────────────────────────────

    public async Task<Result<EmailPreviewDto>> PreviewAsync(string key, EmailPreviewRequest request, CancellationToken ct = default)
    {
        var definition = (await _definitions.FindAsync(key, includeDeleted: true, ct))?.Definition;
        if (definition is null) return UnknownTemplate<EmailPreviewDto>(key);

        var values = await SampleValuesAsync(definition, request.SampleSetId, request.Values, ct);
        var draft = Normalize(request.Subject, request.Preheader, request.Heading, request.BodyHtml, request.TextBody);
        var (content, layout, layoutName, issues) = await PrepareAsync(definition, draft, request.LayoutId, ct);

        // Rendered even when invalid: an admin fixing a typo needs to see the email they are fixing.
        var rendered = EmailTemplateRenderer.Render(definition, content, values, layout, new EmailRenderOptions(request.Dark));
        var preheader = EmailTemplateRenderer.RenderPlainLine(content.Preheader, values);
        return Result.Success(new EmailPreviewDto(rendered.Subject, preheader, rendered.HtmlBody, rendered.TextBody, issues, layoutName));
    }

    /// <summary>
    /// A stored email, rendered as a recipient gets it: the thumbnail on the list and the "as
    /// received" preview. <see cref="EmailRenderQuery.Source"/> "sent" follows the senders' own rule
    /// (this locale's published content, else English's, else the built-in wording; a custom template
    /// that was never published shows its draft, and says so); "draft" renders the locale's draft.
    /// </summary>
    public async Task<Result<EmailRenderedDto>> RenderAsync(string key, EmailRenderQuery query, CancellationToken ct = default)
    {
        var entry = await _definitions.FindAsync(key, includeDeleted: true, ct);
        if (entry is null) return UnknownTemplate<EmailRenderedDto>(key);
        var definition = entry.Definition;
        var locale = EmailLocales.Normalize(query.Locale) ?? EmailLocales.Default;
        var variants = await _unitOfWork.EmailContentVariantRepository.ListAsync(key, ct);

        EmailContentVariant? Active(string code) =>
            variants.FirstOrDefault(v => v.Locale == code && v.Status == EmailCmsConstants.StatusActive);

        EmailTemplateContent content;
        Guid? layoutId;
        string sourceUsed;
        string localeUsed;
        var version = 0;
        var draftOnly = string.Equals(query.Source, "draft", StringComparison.OrdinalIgnoreCase);

        var chosen = draftOnly
            ? Active(locale)
            : EmailLocales.FallbackChain(locale).Select(Active).FirstOrDefault(v => v is { PublishedVersion: > 0 });
        if (draftOnly && chosen is not null)
        {
            content = DraftOf(chosen);
            layoutId = chosen.DraftLayoutId;
            sourceUsed = "DRAFT";
            localeUsed = chosen.Locale;
        }
        else if (!draftOnly && chosen is not null)
        {
            content = new EmailTemplateContent(
                chosen.PublishedSubject ?? string.Empty, chosen.PublishedHeading ?? string.Empty,
                chosen.PublishedBodyHtml ?? string.Empty, chosen.PublishedPreheader ?? string.Empty, chosen.PublishedTextBody);
            layoutId = chosen.PublishedLayoutId;
            sourceUsed = "PUBLISHED";
            localeUsed = chosen.Locale;
            version = chosen.PublishedVersion;
        }
        else if (entry.IsCustom && (Active(locale) ?? Active(EmailLocales.Default)) is { } draft)
        {
            // Nothing published yet: show the draft rather than a placeholder nobody wrote.
            content = DraftOf(draft);
            layoutId = draft.DraftLayoutId;
            sourceUsed = "DRAFT";
            localeUsed = draft.Locale;
        }
        else
        {
            content = definition.Default;
            layoutId = null;
            sourceUsed = "BUILT_IN";
            localeUsed = EmailLocales.Default;
        }

        var values = await SampleValuesAsync(definition, query.SampleSetId, null, ct);
        var (prepared, layout, layoutName, _) = await PrepareAsync(definition, content, layoutId, ct, publishedBlocksOnly: sourceUsed == "PUBLISHED");
        var rendered = EmailTemplateRenderer.Render(definition, prepared, values, layout, new EmailRenderOptions(query.Dark));
        var preheader = EmailTemplateRenderer.RenderPlainLine(prepared.Preheader, values);
        var recipientName = values.TryGetValue(EmailCmsConstants.VariableRecipientName, out var name) ? name : "Linh Nguyen";
        var recipientEmail = values.TryGetValue(EmailCmsConstants.VariableRecipientEmail, out var address) ? address : "linh@example.com";

        return Result.Success(new EmailRenderedDto(
            rendered.Subject,
            preheader,
            rendered.HtmlBody,
            rendered.TextBody,
            layoutName,
            localeUsed,
            sourceUsed,
            version,
            _envelope.FromName,
            _envelope.FromAddress,
            recipientName,
            recipientEmail));
    }

    private static EmailTemplateContent DraftOf(EmailContentVariant v) =>
        new(v.DraftSubject, v.DraftHeading, v.DraftBodyHtml, v.DraftPreheader, v.DraftTextBody);

    public async Task<Result<EmailTestSendDto>> SendTestAsync(
        AdminActorContext actor, string key, EmailTestSendRequest request, CancellationToken ct = default)
    {
        var definition = (await _definitions.FindAsync(key, includeDeleted: false, ct))?.Definition;
        if (definition is null) return UnknownTemplate<EmailTestSendDto>(key);
        if (_emailSender is null)
            return Result.Failure<EmailTestSendDto>("Email sending is not configured on this deployment.", ErrorCodes.ServiceUnavailable);

        var recipients = (request.Recipients ?? [])
            .Select(address => address?.Trim() ?? string.Empty)
            .Where(address => address.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (recipients.Count == 0)
            return Result.Failure<EmailTestSendDto>("Add at least one address to send the test to.", ErrorCodes.ValidationError);
        if (recipients.Count > EmailCmsConstants.MaxTestRecipients)
            return Result.Failure<EmailTestSendDto>($"Send a test to {EmailCmsConstants.MaxTestRecipients} addresses or fewer.", ErrorCodes.ValidationError);
        var invalid = recipients.FirstOrDefault(address => !IsEmailAddress(address));
        if (invalid is not null)
            return Result.Failure<EmailTestSendDto>($"'{invalid}' is not an email address.", ErrorCodes.ValidationError);

        var draft = Normalize(request.Subject, request.Preheader, request.Heading, request.BodyHtml, request.TextBody);
        var (content, layout, _, issues) = await PrepareAsync(definition, draft, request.LayoutId, ct);
        if (issues.Count > 0)
            return Result.Failure<EmailTestSendDto>(issues[0].Message, ErrorCodes.ValidationError);

        var values = await SampleValuesAsync(definition, request.SampleSetId, request.Values, ct);
        var rendered = EmailTemplateRenderer.Render(definition, content, values, layout);
        var subject = $"[Test] {rendered.Subject}";

        var sent = new List<string>();
        var failed = new List<string>();
        foreach (var address in recipients)
        {
            var delivered = await _emailSender.SendEmailAsync(
                new EmailMessage(address, subject, rendered.HtmlBody, TextBody: rendered.TextBody), ct);
            (delivered ? sent : failed).Add(address);
        }

        // Addresses are not written to the audit trail: who received a test is not the platform's
        // business to keep, and the count is what an auditor needs.

        if (sent.Count == 0)
            return Result.Failure<EmailTestSendDto>("The email provider did not accept the test email. Try again in a moment.", ErrorCodes.ServiceUnavailable);
        return Result.Success(new EmailTestSendDto(sent, failed, subject));
    }

    // ── Sample data ──────────────────────────────────────────────────────────────────────

    public async Task<Result<EmailSampleDataSetDto>> SaveSampleSetAsync(
        AdminActorContext actor, string key, Guid? id, SaveSampleDataSetRequest request, CancellationToken ct = default)
    {
        var definition = (await _definitions.FindAsync(key, includeDeleted: false, ct))?.Definition;
        if (definition is null) return UnknownTemplate<EmailSampleDataSetDto>(key);

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0) return Result.Failure<EmailSampleDataSetDto>("Name the sample set.", ErrorCodes.ValidationError);
        if (name.Length > EmailCmsConstants.MaxNameLength)
            return Result.Failure<EmailSampleDataSetDto>($"The name must be {EmailCmsConstants.MaxNameLength} characters or fewer.", ErrorCodes.ValidationError);

        var known = definition.Variables.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (variable, value) in request.Values ?? new Dictionary<string, string>())
        {
            if (!known.Contains(variable))
                return Result.Failure<EmailSampleDataSetDto>($"{{{{{variable}}}}} is not a variable this email has.", ErrorCodes.ValidationError);
            if ((value ?? string.Empty).Length > EmailCmsConstants.MaxSampleValueLength)
                return Result.Failure<EmailSampleDataSetDto>($"The value of {{{{{variable}}}}} is too long.", ErrorCodes.ValidationError);
            values[variable] = value ?? string.Empty;
        }

        var repository = _unitOfWork.EmailSampleDataSetRepository;
        var now = Now;
        EmailSampleDataSet set;
        if (id is { } existingId)
        {
            var found = await repository.GetByIdAsync(existingId, ct);
            if (found is null || found.TemplateKey != definition.Key) return NotFound<EmailSampleDataSetDto>("Sample set not found.");
            set = found;
        }
        else
        {
            var count = (await repository.ListAsync(definition.Key, ct)).Count;
            if (count >= EmailCmsConstants.MaxSampleSets)
                return Result.Failure<EmailSampleDataSetDto>($"An email can have {EmailCmsConstants.MaxSampleSets} sample sets at most.", ErrorCodes.ValidationError);
            set = new EmailSampleDataSet { Id = Guid.CreateVersion7(), TemplateKey = definition.Key, CreatedAt = now, CreatedBy = actor.ActorId };
            await repository.AddAsync(set, ct);
        }

        set.Name = name;
        set.Values = JsonSerializer.Serialize(values);
        set.UpdatedAt = now;
        set.UpdatedBy = actor.ActorId;
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(ToSampleDto(set));
    }

    public async Task<Result> DeleteSampleSetAsync(AdminActorContext actor, string key, Guid id, CancellationToken ct = default)
    {
        var definition = (await _definitions.FindAsync(key, includeDeleted: false, ct))?.Definition;
        if (definition is null) return Result.Failure($"The platform sends no email called '{key}'.", ErrorCodes.NotFound);

        var repository = _unitOfWork.EmailSampleDataSetRepository;
        var set = await repository.GetByIdAsync(id, ct);
        if (set is null || set.TemplateKey != definition.Key) return Result.Failure("Sample set not found.", ErrorCodes.NotFound);

        repository.Remove(set);
        await _unitOfWork.SaveChangesAsync();
        return Result.Success();
    }

    // ── Bulk ─────────────────────────────────────────────────────────────────────────────

    public async Task<Result<BulkResultDto>> BulkAsync(AdminActorContext actor, EmailBulkRequest request, CancellationToken ct = default)
    {
        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("publish" or "discard" or "archive" or "duplicate"))
            return Result.Failure<BulkResultDto>("Action must be publish, discard, archive or duplicate.", ErrorCodes.ValidationError);
        var keys = (request.Keys ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0) return Result.Failure<BulkResultDto>("Select at least one email.", ErrorCodes.ValidationError);
        if (keys.Count > AnnouncementConstants.MaxBulkItems)
            return Result.Failure<BulkResultDto>("Too many emails in one action.", ErrorCodes.ValidationError);

        var locale = request.Locale is null ? null : EmailLocales.Normalize(request.Locale);
        if (request.Locale is not null && locale is null)
            return Result.Failure<BulkResultDto>($"Locale must be one of {string.Join(", ", EmailLocales.Supported)}.", ErrorCodes.ValidationError);

        var results = new List<BulkItemResultDto>();
        foreach (var key in keys)
        {
            if (await _definitions.FindAsync(key, includeDeleted: false, ct) is null)
            {
                results.Add(new BulkItemResultDto(key, false, "Unknown email."));
                continue;
            }

            if (action == "duplicate")
            {
                var copied = await DuplicateAsync(actor, key, locale ?? EmailLocales.Default,
                    new DuplicateEmailVariantRequest(request.TargetLocale ?? string.Empty), ct);
                results.Add(new BulkItemResultDto(key, copied.IsSuccess, copied.Error));
                continue;
            }

            var variants = (await _unitOfWork.EmailContentVariantRepository.ListAsync(key, ct))
                .Where(v => locale is null || v.Locale == locale)
                .ToList();
            var targets = action switch
            {
                "publish" => variants.Where(v => v.Status == EmailCmsConstants.StatusActive && HasDraftChanges(v)).ToList(),
                "discard" => variants.Where(HasDraftChanges).ToList(),
                _ => variants.Where(v => v.Status == EmailCmsConstants.StatusActive).ToList(),
            };
            if (targets.Count == 0)
            {
                results.Add(new BulkItemResultDto(key, false, "Nothing to do for this email."));
                continue;
            }

            string? firstError = null;
            foreach (var variant in targets)
            {
                // Each arm is widened to the non-generic Result: the three results differ in their
                // value's nullability (a discard can delete the row), and only success matters here.
                Result outcome = action switch
                {
                    "publish" => (Result)await PublishAsync(actor, key, variant.Locale, new PublishEmailRequest(), ct),
                    "discard" => (Result)await DiscardDraftAsync(actor, key, variant.Locale, ct),
                    _ => (Result)await ArchiveAsync(actor, key, variant.Locale, ct),
                };
                if (!outcome.IsSuccess) firstError ??= $"{variant.Locale}: {outcome.Error}";
            }
            results.Add(new BulkItemResultDto(key, firstError is null, firstError));
        }

        return Result.Success(new BulkResultDto(results));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private async Task PublishVariantAsync(EmailContentVariant variant, Guid actorId, string? note, CancellationToken ct)
    {
        var now = Now;
        variant.PublishedSubject = variant.DraftSubject;
        variant.PublishedPreheader = variant.DraftPreheader;
        variant.PublishedHeading = variant.DraftHeading;
        variant.PublishedBodyHtml = variant.DraftBodyHtml;
        variant.PublishedTextBody = variant.DraftTextBody;
        variant.PublishedLayoutId = variant.DraftLayoutId;
        variant.PublishedVersion += 1;
        variant.PublishedAt = now;
        variant.PublishedBy = actorId;

        await _unitOfWork.EmailCmsVersionRepository.AddAsync(new EmailCmsVersion
        {
            Id = Guid.CreateVersion7(),
            OwnerType = EmailCmsConstants.OwnerContent,
            OwnerId = variant.Id,
            Version = variant.PublishedVersion,
            Action = EmailCmsConstants.ActionPublished,
            Snapshot = JsonSerializer.Serialize(ContentSnapshot(variant)),
            Note = note,
            CreatedBy = actorId,
            CreatedAt = now,
        }, ct);
    }

    /// <summary>Why the draft cannot go out, checked against published blocks — exactly what a send would use.</summary>
    private async Task<string?> PublishErrorAsync(EmailTemplateDefinition definition, EmailContentVariant variant, CancellationToken ct)
    {
        var draft = new EmailTemplateContent(variant.DraftSubject, variant.DraftHeading, variant.DraftBodyHtml, variant.DraftPreheader, variant.DraftTextBody);
        var layoutError = await LayoutChoiceErrorAsync(variant.DraftLayoutId, ct, requirePublished: true);
        if (layoutError is not null) return layoutError;
        var (_, _, _, issues) = await PrepareAsync(definition, draft, variant.DraftLayoutId, ct, publishedBlocksOnly: true);
        return issues.Count > 0 ? issues[0].Message : null;
    }

    /// <summary>
    /// The content with partials expanded, the layout it renders into, and everything wrong with
    /// either. Previews use a block's draft when it has never been published; publishing does not.
    /// </summary>
    private async Task<(EmailTemplateContent Content, EmailLayout? Layout, string LayoutName, IReadOnlyList<EmailTemplateIssue> Issues)> PrepareAsync(
        EmailTemplateDefinition definition,
        EmailTemplateContent draft,
        Guid? layoutId,
        CancellationToken ct,
        bool publishedBlocksOnly = false)
    {
        var blocks = await _unitOfWork.EmailBlockRepository.ListAsync(null, ct);
        var partials = blocks
            .Where(b => b.Kind == EmailCmsConstants.KindPartial && b.Status == EmailCmsConstants.StatusActive)
            .Select(b => (b.Key, Html: b.PublishedHtml ?? (publishedBlocksOnly ? null : b.DraftHtml)))
            .Where(p => p.Html is not null)
            .ToDictionary(p => p.Key, p => p.Html!, StringComparer.Ordinal);

        var content = new EmailTemplateContent(
            EmailTemplateRenderer.ExpandPartials(draft.Subject, partials),
            EmailTemplateRenderer.ExpandPartials(draft.Heading, partials),
            EmailTemplateRenderer.ExpandPartials(draft.BodyHtml, partials),
            EmailTemplateRenderer.ExpandPartials(draft.Preheader, partials),
            draft.TextBody is null ? null : EmailTemplateRenderer.ExpandPartials(draft.TextBody, partials));
        var issues = EmailTemplateRenderer.Validate(definition, content).ToList();

        var layoutBlock = layoutId is { } id
            ? blocks.FirstOrDefault(b => b.Id == id && b.Kind == EmailCmsConstants.KindLayout)
            : blocks.FirstOrDefault(b => b.Kind == EmailCmsConstants.KindLayout && b.IsDefault && b.Status == EmailCmsConstants.StatusActive && b.PublishedHtml is not null);

        EmailLayout? layout = null;
        var layoutName = "Built-in layout";
        if (layoutBlock is not null)
        {
            var html = layoutBlock.PublishedHtml ?? (publishedBlocksOnly ? null : layoutBlock.DraftHtml);
            if (html is not null)
            {
                layout = new EmailLayout(
                    EmailTemplateRenderer.ExpandPartials(html, partials),
                    layoutBlock.PublishedHtml is not null ? layoutBlock.PublishedText : layoutBlock.DraftText,
                    layoutBlock.PublishedHtml is not null ? layoutBlock.PublishedDarkCss : layoutBlock.DraftDarkCss);
                layoutName = layoutBlock.Name;
                issues.AddRange(EmailTemplateRenderer.ValidateLayout(layout)
                    .Select(issue => issue with { Field = "layout", Message = $"Layout \"{layoutBlock.Name}\": {issue.Message}" }));
            }
        }

        return (content, layout, layoutName, issues);
    }

    private async Task<string?> LayoutChoiceErrorAsync(Guid? layoutId, CancellationToken ct, bool requirePublished = false)
    {
        if (layoutId is not { } id) return null;
        var block = await _unitOfWork.EmailBlockRepository.GetByIdAsync(id, ct);
        if (block is null || block.Kind != EmailCmsConstants.KindLayout) return "That layout does not exist.";
        if (block.Status == EmailCmsConstants.StatusArchived) return $"The layout \"{block.Name}\" is archived.";
        if (requirePublished && block.PublishedHtml is null) return $"Publish the layout \"{block.Name}\" before publishing an email that uses it.";
        return null;
    }

    private async Task<IReadOnlyDictionary<string, string>> SampleValuesAsync(
        EmailTemplateDefinition definition, Guid? sampleSetId, IReadOnlyDictionary<string, string>? overrides, CancellationToken ct)
    {
        var values = new Dictionary<string, string>(EmailTemplateCatalog.SampleValues(definition), StringComparer.Ordinal);
        if (sampleSetId is { } id && id != Guid.Empty)
        {
            var set = await _unitOfWork.EmailSampleDataSetRepository.GetByIdAsync(id, ct);
            if (set is not null && set.TemplateKey == definition.Key)
            {
                foreach (var (name, value) in ReadValues(set.Values)) values[name] = value;
            }
        }
        foreach (var (name, value) in overrides ?? new Dictionary<string, string>())
        {
            if (values.ContainsKey(name)) values[name] = value ?? string.Empty;
        }
        return values;
    }

    private static EmailTemplateListItemDto ListItem(
        EmailDefinitionEntry entry,
        IReadOnlyList<EmailContentVariant> variants,
        IReadOnlyList<EmailBlock> layouts,
        IEnumerable<EmailDeliveryStat> stats)
    {
        var definition = entry.Definition;
        var english = variants.FirstOrDefault(v => v.Locale == EmailLocales.Default && v.Status == EmailCmsConstants.StatusActive);
        // What goes out in English now; a custom template that was never published shows its draft.
        var (subject, preheader) = english switch
        {
            { PublishedVersion: > 0 } => (english.PublishedSubject ?? definition.Default.Subject, english.PublishedPreheader ?? string.Empty),
            not null when entry.IsCustom => (english.DraftSubject, english.DraftPreheader),
            _ => (definition.Default.Subject, definition.Default.Preheader),
        };
        var samples = EmailTemplateCatalog.SampleValues(definition);
        var layoutId = english?.PublishedLayoutId;
        var layout = layoutId is { } id
            ? layouts.FirstOrDefault(l => l.Id == id)
            : layouts.FirstOrDefault(l => l.IsDefault && l.Status == EmailCmsConstants.StatusActive && l.PublishedHtml is not null);
        var latest = variants.OrderByDescending(v => v.DraftUpdatedAt).FirstOrDefault();
        var statRows = stats.ToList();

        return new EmailTemplateListItemDto(
            definition.Key,
            definition.Name,
            definition.Description,
            definition.Service,
            definition.Provider,
            definition.Trigger,
            definition.IsLive,
            definition.DormantReason,
            VariableDtos(entry),
            subject,
            layout?.Name,
            variants.Select(ToSummary).ToList(),
            variants.Any(v => v.Status == EmailCmsConstants.StatusActive && HasDraftChanges(v)),
            latest?.DraftUpdatedAt,
            latest?.DraftUpdatedBy,
            new EmailDeliveryTotalsDto(statRows.Sum(s => s.SentCount), statRows.Sum(s => s.FailedCount)))
        {
            IsCustom = entry.IsCustom,
            Category = entry.Custom?.Category ?? EmailTemplateListItemDto.CategoryBuiltIn,
            Status = entry.Custom?.Status ?? EmailCmsConstants.StatusActive,
            DeletedAt = entry.Custom?.DeletedAt,
            DeleteReason = entry.Custom?.DeleteReason,
            RenderedSubject = EmailTemplateRenderer.RenderPlainLine(subject, samples),
            RenderedPreheader = EmailTemplateRenderer.RenderPlainLine(preheader, samples),
            CreatedAt = entry.Custom?.CreatedAt,
            CreatedBy = entry.Custom?.CreatedBy,
        };
    }

    internal static IReadOnlyList<EmailTemplateVariableDto> VariableDtos(EmailDefinitionEntry entry)
    {
        var declared = entry.Custom is null
            ? new Dictionary<string, EmailCustomVariable>()
            : CustomEmailDefinitions.ReadVariables(entry.Custom.Variables).ToDictionary(v => v.Name, StringComparer.Ordinal);
        return entry.Definition.Variables
            .Select(v => new EmailTemplateVariableDto(
                v.Name, v.Description, v.Sample,
                declared.TryGetValue(v.Name, out var own) ? own.Required : v.Required,
                v.Multiline)
            {
                Type = declared.TryGetValue(v.Name, out var custom) ? custom.Type : (v.Multiline ? EmailCmsConstants.VariableMultiline : EmailCmsConstants.VariableText),
                Label = declared.TryGetValue(v.Name, out var labelled) ? labelled.Label : null,
                Implicit = entry.IsCustom && CustomEmailDefinitions.IsImplicit(v.Name),
            })
            .ToList();
    }

    public static bool HasDraftChanges(EmailContentVariant v) =>
        v.PublishedVersion == 0
        || v.DraftSubject != v.PublishedSubject
        || v.DraftPreheader != v.PublishedPreheader
        || v.DraftHeading != v.PublishedHeading
        || v.DraftBodyHtml != v.PublishedBodyHtml
        || v.DraftTextBody != v.PublishedTextBody
        || v.DraftLayoutId != v.PublishedLayoutId;

    private static EmailVariantSummaryDto ToSummary(EmailContentVariant v) =>
        new(v.Id, v.Locale, v.Status, v.PublishedVersion, HasDraftChanges(v), v.PublishedAt, v.PublishedBy, v.DraftUpdatedAt, v.DraftUpdatedBy);

    private static EmailVariantDto ToVariantDto(EmailContentVariant v) =>
        new(
            v.Id,
            v.Locale,
            v.Status,
            new EmailContentFieldsDto(v.DraftSubject, v.DraftPreheader, v.DraftHeading, v.DraftBodyHtml, v.DraftTextBody, v.DraftLayoutId),
            v.PublishedVersion > 0
                ? new EmailContentFieldsDto(v.PublishedSubject ?? string.Empty, v.PublishedPreheader ?? string.Empty,
                    v.PublishedHeading ?? string.Empty, v.PublishedBodyHtml ?? string.Empty, v.PublishedTextBody, v.PublishedLayoutId)
                : null,
            v.PublishedVersion,
            HasDraftChanges(v),
            v.PublishedAt,
            v.PublishedBy,
            v.DraftUpdatedAt,
            v.DraftUpdatedBy,
            v.ArchivedAt);

    private static EmailContentFieldsDto FromDefault(EmailTemplateContent content) =>
        new(content.Subject, content.Preheader, content.Heading, content.BodyHtml, content.TextBody, null);

    private static EmailSampleDataSetDto ToSampleDto(EmailSampleDataSet set) =>
        new(set.Id, set.Name, ReadValues(set.Values), BuiltIn: false, set.UpdatedAt);

    private static EmailCmsVersionDto ToVersionDto(EmailCmsVersion v) =>
        new(v.Version, v.Action, ReadSnapshot(v.Snapshot), v.Note, v.CreatedBy, v.CreatedAt);

    private static Dictionary<string, string?> ContentSnapshot(EmailContentVariant v) => new()
    {
        ["subject"] = v.PublishedSubject,
        ["preheader"] = v.PublishedPreheader,
        ["heading"] = v.PublishedHeading,
        ["bodyHtml"] = v.PublishedBodyHtml,
        ["textBody"] = v.PublishedTextBody,
        ["layoutId"] = v.PublishedLayoutId?.ToString(),
    };

    public static IReadOnlyDictionary<string, string?> ReadSnapshot(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? new Dictionary<string, string?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string?>();
        }
    }

    private static IReadOnlyDictionary<string, string> ReadValues(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    internal static EmailContentVariant NewVariant(string key, string locale, Guid actorId, DateTime now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TemplateKey = key,
            Locale = locale,
            Status = EmailCmsConstants.StatusActive,
            DraftSubject = string.Empty,
            DraftPreheader = string.Empty,
            DraftHeading = string.Empty,
            DraftBodyHtml = string.Empty,
            CreatedAt = now,
            CreatedBy = actorId,
            DraftUpdatedAt = now,
            DraftUpdatedBy = actorId,
        };

    internal static void ApplyDraft(EmailContentVariant variant, EmailTemplateContent draft, Guid? layoutId, Guid actorId, DateTime now)
    {
        variant.DraftSubject = draft.Subject;
        variant.DraftPreheader = draft.Preheader;
        variant.DraftHeading = draft.Heading;
        variant.DraftBodyHtml = draft.BodyHtml;
        variant.DraftTextBody = string.IsNullOrWhiteSpace(draft.TextBody) ? null : draft.TextBody;
        variant.DraftLayoutId = layoutId;
        variant.DraftUpdatedAt = now;
        variant.DraftUpdatedBy = actorId;
    }

    internal static EmailTemplateContent Normalize(string? subject, string? preheader, string? heading, string? body, string? text) =>
        new(
            (subject ?? string.Empty).Trim(),
            (heading ?? string.Empty).Trim(),
            body ?? string.Empty,
            (preheader ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(text) ? null : text);

    /// <summary>A draft may be incomplete, but it must fit its columns.</summary>
    internal static string? DraftShapeError(EmailTemplateContent draft)
    {
        if (draft.Subject.Length > EmailTemplateRenderer.MaxSubjectLength) return "The subject is too long.";
        if (draft.Preheader.Length > EmailTemplateRenderer.MaxPreheaderLength) return "The preheader is too long.";
        if (draft.Heading.Length > EmailTemplateRenderer.MaxHeadingLength) return "The heading is too long.";
        if (draft.BodyHtml.Length > EmailTemplateRenderer.MaxBodyLength) return "The body is too long.";
        if ((draft.TextBody?.Length ?? 0) > EmailTemplateRenderer.MaxTextLength) return "The plain-text version is too long.";
        return null;
    }

    private async Task<(EmailTemplateDefinition? Definition, string? Locale, (string Message, string Code)? Error)> ResolveAsync(
        string key, string locale, CancellationToken ct)
    {
        var entry = await _definitions.FindAsync(key, includeDeleted: true, ct);
        if (entry is null) return (null, null, ($"The platform sends no email called '{key}'.", ErrorCodes.NotFound));
        if (entry.IsDeleted)
            return (null, null, ("This template is deleted. Restore it before changing it.", ErrorCodes.InvalidState));
        var definition = entry.Definition;
        var normalized = EmailLocales.Normalize(locale);
        if (normalized is null)
            return (definition, null, ($"Locale must be one of {string.Join(", ", EmailLocales.Supported)}.", ErrorCodes.ValidationError));
        return (definition, normalized, null);
    }

    private DateOnly StatsSince(int days) => DateOnly.FromDateTime(Now).AddDays(-(days - 1));

    private static bool SameInstant(DateTime a, DateTime b) =>
        Math.Abs((a.ToUniversalTime() - b.ToUniversalTime()).TotalMilliseconds) < 1;

    internal static bool IsEmailAddress(string address)
    {
        try
        {
            var parsed = new MailAddress(address);
            return string.Equals(parsed.Address, address, StringComparison.OrdinalIgnoreCase) && address.Contains('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static Result<T> UnknownTemplate<T>(string key) =>
        Result.Failure<T>($"The platform sends no email called '{key}'.", ErrorCodes.NotFound);

    private static Result<T> NotFound<T>(string message) => Result.Failure<T>(message, ErrorCodes.NotFound);
}
