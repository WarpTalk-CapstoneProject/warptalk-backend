using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WarpTalk.NotificationService.Application.DTOs.EmailTemplates;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Email;

namespace WarpTalk.NotificationService.Application.Services.EmailCms;

/// <summary>Limits on audience sends. Bound from configuration section "EmailCampaigns".</summary>
public sealed class EmailCampaignOptions
{
    /// <summary>The most people one send may reach.</summary>
    public int MaxRecipients { get; set; } = 5_000;

    /// <summary>Provider throughput: the worker never hands over more than this per minute.</summary>
    public int SendsPerMinute { get; set; } = 60;

    /// <summary>How many sends one admin may start per hour.</summary>
    public int MaxSendsPerAdminPerHour { get; set; } = 5;

    /// <summary>How far the confirmed count may drift from the real one before the admin must re-confirm.</summary>
    public int RecipientDriftTolerance { get; set; } = 5;
}

public interface IEmailCampaignService
{
    Task<Result<EmailSendEstimateDto>> EstimateAsync(string key, EmailSendEstimateRequest request, CancellationToken ct = default);
    Task<Result<EmailCampaignDto>> CreateAsync(AdminActorContext actor, string key, CreateEmailSendRequest request, CancellationToken ct = default);
    Task<Result<IReadOnlyList<EmailCampaignDto>>> ListAsync(string key, CancellationToken ct = default);
    Task<Result<EmailCampaignDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<EmailCampaignRecipientPageDto>> RecipientsAsync(Guid id, string? status, int page, int pageSize, CancellationToken ct = default);
    Task<Result<EmailCampaignDto>> CancelAsync(AdminActorContext actor, Guid id, CancellationToken ct = default);

    /// <summary>
    /// Whether this template can be an announcement's email channel: a custom template, not
    /// deleted, with English published. Null when it can.
    /// </summary>
    Task<string?> AnnouncementChannelErrorAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Queues the email channel of an announcement being published, to its audience, at its start.
    /// Adds the campaign without saving: the caller saves it with the announcement.
    /// </summary>
    Task<Result<EmailCampaign>> QueueForAnnouncementAsync(Guid actorId, Announcement announcement, CancellationToken ct = default);

    /// <summary>Cancels an announcement's email that has not started (it was unpublished or archived).</summary>
    Task CancelQueuedForAnnouncementAsync(Guid actorId, Announcement announcement, CancellationToken ct = default);

    /// <summary>
    /// One step of the send worker: resolve a due send's recipients, or hand the next batch to the
    /// provider. Returns false when there was nothing to do.
    /// </summary>
    Task<bool> ProcessNextAsync(int batchSize, CancellationToken ct = default);
}

/// <summary>
/// Audience sends of custom templates. Only PUBLISHED content goes out — each person gets their
/// language's published version, or English's — so an admin drafting a change never alters a send
/// in progress. Marketing and announcement emails skip people who turned that kind of email off.
/// Every send is audited at the controller; each recipient's outcome is kept here.
/// </summary>
public sealed class EmailCampaignService : IEmailCampaignService
{
    private const int MaxListed = 50;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IUnitOfWork _unitOfWork;
    private readonly IEmailDefinitionProvider _definitions;
    private readonly IEmailAudienceResolver _audience;
    private readonly IEmailTemplateSource _published;
    private readonly IEmailSender? _sender;
    private readonly EmailCampaignOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<EmailCampaignService> _logger;

    public EmailCampaignService(
        IUnitOfWork unitOfWork,
        IEmailDefinitionProvider definitions,
        IEmailAudienceResolver audience,
        IEmailTemplateSource published,
        EmailCampaignOptions? options = null,
        IEmailSender? sender = null,
        TimeProvider? timeProvider = null,
        ILogger<EmailCampaignService>? logger = null)
    {
        _unitOfWork = unitOfWork;
        _definitions = definitions;
        _audience = audience;
        _published = published;
        _options = options ?? new EmailCampaignOptions();
        _sender = sender;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<EmailCampaignService>.Instance;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ── Estimate and start ──────────────────────────────────────────────────────────────────

    public async Task<Result<EmailSendEstimateDto>> EstimateAsync(string key, EmailSendEstimateRequest request, CancellationToken ct = default)
    {
        var (entry, error) = await SendableAsync(key, ct);
        if (error is not null) return Result.Failure<EmailSendEstimateDto>(error.Value.Message, error.Value.Code);
        var (spec, specError) = EmailAudienceSpec.From(request.Audience);
        if (specError is not null) return Result.Failure<EmailSendEstimateDto>(specError, ErrorCodes.ValidationError);

        var resolved = await _audience.ResolveAsync(spec!, _options.MaxRecipients, ct);
        if (!resolved.IsSuccess) return Result.Failure<EmailSendEstimateDto>(resolved.Error!, resolved.ErrorCode);
        var optedOut = await OptedOutAsync(entry!, resolved.Value!.Members, ct);
        var reachable = resolved.Value.Members.Where(m => !optedOut.Contains(m.UserId)).ToList();
        var published = await PublishedLocalesAsync(key, ct);

        return Result.Success(new EmailSendEstimateDto(
            reachable.Count,
            optedOut.Count,
            reachable.GroupBy(m => m.Locale).OrderBy(g => Array.IndexOf(EmailLocales.Supported, g.Key))
                .Select(g => new EmailStatsLocaleCountDto(g.Key, g.Count())).ToList(),
            reachable.Select(m => m.Locale).Distinct().Where(l => !published.Contains(l)).ToList(),
            _options.MaxRecipients,
            _options.SendsPerMinute));
    }

    public async Task<Result<EmailCampaignDto>> CreateAsync(
        AdminActorContext actor, string key, CreateEmailSendRequest request, CancellationToken ct = default)
    {
        var (entry, error) = await SendableAsync(key, ct);
        if (error is not null) return Result.Failure<EmailCampaignDto>(error.Value.Message, error.Value.Code);
        var (spec, specError) = EmailAudienceSpec.From(request.Audience);
        if (specError is not null) return Result.Failure<EmailCampaignDto>(specError, ErrorCodes.ValidationError);
        var (values, valuesError) = ValidateValues(entry!, request.Values);
        if (valuesError is not null) return Result.Failure<EmailCampaignDto>(valuesError, ErrorCodes.ValidationError);

        var now = Now;
        var recent = await _unitOfWork.EmailCampaignRepository.CountCreatedSinceAsync(actor.ActorId, now.AddHours(-1), ct);
        if (recent >= _options.MaxSendsPerAdminPerHour)
        {
            return Result.Failure<EmailCampaignDto>(
                $"You can start {_options.MaxSendsPerAdminPerHour} sends an hour. Try again later.", ErrorCodes.RateLimitExceeded);
        }

        var scheduledAt = request.ScheduledAt?.ToUniversalTime() ?? now;
        if (scheduledAt < now.AddMinutes(-1)) return Result.Failure<EmailCampaignDto>("The send time is in the past.", ErrorCodes.ValidationError);
        if (scheduledAt > now.AddDays(90)) return Result.Failure<EmailCampaignDto>("Schedule it within 90 days.", ErrorCodes.ValidationError);

        var resolved = await _audience.ResolveAsync(spec!, _options.MaxRecipients, ct);
        if (!resolved.IsSuccess) return Result.Failure<EmailCampaignDto>(resolved.Error!, resolved.ErrorCode);
        var optedOut = await OptedOutAsync(entry!, resolved.Value!.Members, ct);
        var reachable = resolved.Value.Members.Count(m => !optedOut.Contains(m.UserId));
        if (reachable == 0) return Result.Failure<EmailCampaignDto>("Nobody in this audience can be emailed.", ErrorCodes.ValidationError);
        if (Math.Abs(reachable - request.ExpectedRecipients) > _options.RecipientDriftTolerance)
        {
            return Result.Failure<EmailCampaignDto>(
                $"The audience is now {reachable:N0} people, not {request.ExpectedRecipients:N0}. Review the new number and confirm again.",
                ErrorCodes.Conflict);
        }

        var campaign = NewCampaign(key, EmailCmsConstants.CampaignSourceManual, null, spec!, values, scheduledAt, actor.ActorId, now);
        await _unitOfWork.EmailCampaignRepository.AddAsync(campaign, ct);
        await AddRecipientsAsync(campaign, resolved.Value.Members, optedOut, entry!, ct);
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(ToDto(campaign, entry!.Definition.Name));
    }

    // ── Reads ──────────────────────────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<EmailCampaignDto>>> ListAsync(string key, CancellationToken ct = default)
    {
        var entry = await _definitions.FindAsync(key, includeDeleted: true, ct);
        if (entry is null) return Result.Failure<IReadOnlyList<EmailCampaignDto>>($"There is no template called '{key}'.", ErrorCodes.NotFound);
        var campaigns = await _unitOfWork.EmailCampaignRepository.ListForTemplateAsync(key, MaxListed, ct);
        IReadOnlyList<EmailCampaignDto> items = campaigns.Select(c => ToDto(c, entry.Definition.Name)).ToList();
        return Result.Success(items);
    }

    public async Task<Result<EmailCampaignDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var campaign = await _unitOfWork.EmailCampaignRepository.GetByIdAsync(id, ct);
        if (campaign is null) return Result.Failure<EmailCampaignDto>("Send not found.", ErrorCodes.NotFound);
        var entry = await _definitions.FindAsync(campaign.TemplateKey, includeDeleted: true, ct);
        return Result.Success(ToDto(campaign, entry?.Definition.Name ?? campaign.TemplateKey));
    }

    public async Task<Result<EmailCampaignRecipientPageDto>> RecipientsAsync(
        Guid id, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        if (await _unitOfWork.EmailCampaignRepository.GetByIdAsync(id, ct) is null)
            return Result.Failure<EmailCampaignRecipientPageDto>("Send not found.", ErrorCodes.NotFound);
        var filter = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var (items, total) = await _unitOfWork.EmailCampaignRecipientRepository.PageAsync(id, filter, page, pageSize, ct);
        return Result.Success(new EmailCampaignRecipientPageDto(
            items.Select(r => new EmailCampaignRecipientDto(r.UserId, r.Email, r.FullName, r.Locale, r.Status, r.Error, r.SentAt)).ToList(),
            total, page, pageSize));
    }

    public async Task<Result<EmailCampaignDto>> CancelAsync(AdminActorContext actor, Guid id, CancellationToken ct = default)
    {
        var campaign = await _unitOfWork.EmailCampaignRepository.GetByIdAsync(id, ct);
        if (campaign is null) return Result.Failure<EmailCampaignDto>("Send not found.", ErrorCodes.NotFound);
        if (campaign.Status is not (EmailCmsConstants.CampaignQueued or EmailCmsConstants.CampaignSending))
            return Result.Failure<EmailCampaignDto>("It has already finished.", ErrorCodes.InvalidState);

        await CancelAsync(campaign, actor.ActorId, "Cancelled before it was sent.", ct);
        await _unitOfWork.SaveChangesAsync();
        var entry = await _definitions.FindAsync(campaign.TemplateKey, includeDeleted: true, ct);
        return Result.Success(ToDto(campaign, entry?.Definition.Name ?? campaign.TemplateKey));
    }

    // ── Announcements ──────────────────────────────────────────────────────────────────────

    public async Task<string?> AnnouncementChannelErrorAsync(string key, CancellationToken ct = default)
    {
        var (_, error) = await SendableAsync(key, ct);
        return error?.Message;
    }

    public async Task<Result<EmailCampaign>> QueueForAnnouncementAsync(Guid actorId, Announcement announcement, CancellationToken ct = default)
    {
        if (announcement.EmailTemplateKey is not { } key) return Result.Failure<EmailCampaign>("No email channel.", ErrorCodes.ValidationError);
        var (entry, error) = await SendableAsync(key, ct);
        if (error is not null) return Result.Failure<EmailCampaign>(error.Value.Message, error.Value.Code);

        var spec = new EmailAudienceSpec(
            announcement.AudienceMode,
            announcement.AudiencePlanSlugs.ToList(),
            announcement.AudienceWorkspaceIds.ToList(),
            announcement.TargetRoles.ToList(),
            announcement.TargetLocales.ToList(),
            announcement.NewUsersWithinDays);
        var values = CustomEmailDefinitions.ReadVariables(entry!.Custom!.Variables)
            .ToDictionary(v => v.Name, v => v.Sample, StringComparer.Ordinal);
        values[EmailCmsConstants.VariableAnnouncementTitle] = announcement.Title;
        values[EmailCmsConstants.VariableAnnouncementText] = PlainText(announcement.BodyMarkdown);
        values[EmailCmsConstants.VariableAnnouncementLink] = announcement.CtaUrl ?? string.Empty;

        var now = Now;
        var campaign = NewCampaign(
            key, EmailCmsConstants.CampaignSourceAnnouncement, announcement.Id, spec, values,
            announcement.StartsAt is { } start && start > now ? start : now, actorId, now);
        await _unitOfWork.EmailCampaignRepository.AddAsync(campaign, ct);
        return Result.Success(campaign);
    }

    public async Task CancelQueuedForAnnouncementAsync(Guid actorId, Announcement announcement, CancellationToken ct = default)
    {
        if (announcement.EmailCampaignId is not { } id) return;
        var campaign = await _unitOfWork.EmailCampaignRepository.GetByIdAsync(id, ct);
        if (campaign is null) { announcement.EmailCampaignId = null; return; }
        if (campaign.Status != EmailCmsConstants.CampaignQueued) return;
        // Only a send that has not started is withdrawn; one that went out stays on the record.
        await CancelAsync(campaign, actorId, "The announcement changed or was unpublished before its email went out.", ct);
        announcement.EmailCampaignId = null;
    }

    // ── The worker's step ──────────────────────────────────────────────────────────────────

    public async Task<bool> ProcessNextAsync(int batchSize, CancellationToken ct = default)
    {
        var campaign = await _unitOfWork.EmailCampaignRepository.NextDueAsync(Now, ct);
        if (campaign is null) return false;

        var entry = await _definitions.FindAsync(campaign.TemplateKey, includeDeleted: false, ct);
        if (entry is null)
        {
            await FailAsync(campaign, "The template was deleted before the send finished.", ct);
            return true;
        }

        if (campaign.Status == EmailCmsConstants.CampaignQueued)
        {
            campaign.Status = EmailCmsConstants.CampaignSending;
            campaign.StartedAt = Now;
            // Announcement sends resolve their audience when they go live, not when scheduled.
            if (!await _unitOfWork.EmailCampaignRecipientRepository.AnyAsync(campaign.Id, ct))
            {
                var spec = JsonSerializer.Deserialize<EmailAudienceDto>(campaign.Audience, Json);
                var (parsed, _) = EmailAudienceSpec.From(spec);
                var resolved = parsed is null
                    ? Result.Failure<EmailAudienceResult>("The audience could not be read.", ErrorCodes.ValidationError)
                    : await _audience.ResolveAsync(parsed, _options.MaxRecipients, ct);
                if (!resolved.IsSuccess)
                {
                    await FailAsync(campaign, resolved.Error ?? "The audience could not be resolved.", ct);
                    return true;
                }
                var optedOut = await OptedOutAsync(entry, resolved.Value!.Members, ct);
                await AddRecipientsAsync(campaign, resolved.Value.Members, optedOut, entry, ct);
            }
            await _unitOfWork.SaveChangesAsync();
            return true;
        }

        var batch = await _unitOfWork.EmailCampaignRecipientRepository.NextPendingAsync(campaign.Id, Math.Max(1, batchSize), ct);
        if (batch.Count == 0)
        {
            campaign.Status = EmailCmsConstants.CampaignCompleted;
            campaign.CompletedAt = Now;
            await _unitOfWork.SaveChangesAsync();
            return true;
        }
        if (_sender is null)
        {
            await FailAsync(campaign, "Email sending is not configured on this deployment.", ct);
            return true;
        }

        var values = ReadValues(campaign.Values);
        var contentByLocale = new Dictionary<string, StoredEmailTemplate?>(StringComparer.Ordinal);
        foreach (var recipient in batch)
        {
            if (!contentByLocale.TryGetValue(recipient.Locale, out var stored))
            {
                stored = await _published.FindActiveAsync(campaign.TemplateKey, recipient.Locale, ct);
                contentByLocale[recipient.Locale] = stored;
            }
            if (stored is null)
            {
                recipient.Status = EmailCmsConstants.RecipientFailed;
                recipient.Error = "Nothing is published for this template.";
                campaign.FailedCount++;
                continue;
            }

            var personal = new Dictionary<string, string>(values, StringComparer.Ordinal)
            {
                [EmailCmsConstants.VariableRecipientName] = string.IsNullOrWhiteSpace(recipient.FullName) ? recipient.Email : recipient.FullName!,
                [EmailCmsConstants.VariableRecipientEmail] = recipient.Email,
            };
            var rendered = Render(entry.Definition, stored, personal);
            bool delivered;
            try
            {
                delivered = await _sender.SendEmailAsync(new EmailMessage(recipient.Email, rendered.Subject, rendered.HtmlBody, TextBody: rendered.TextBody), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Send {CampaignId}: the provider threw for one recipient.", campaign.Id);
                delivered = false;
            }

            if (delivered)
            {
                recipient.Status = EmailCmsConstants.RecipientSent;
                recipient.SentAt = Now;
                campaign.SentCount++;
            }
            else
            {
                recipient.Status = EmailCmsConstants.RecipientFailed;
                recipient.Error = "The email provider did not accept it.";
                campaign.FailedCount++;
            }
            await _unitOfWork.EmailDeliveryStatRepository.IncrementAsync(
                campaign.TemplateKey, stored.Locale, DateOnly.FromDateTime(Now), delivered, ct);
        }
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────

    private async Task<(EmailDefinitionEntry? Entry, (string Message, string Code)? Error)> SendableAsync(string key, CancellationToken ct)
    {
        var entry = await _definitions.FindAsync(key, includeDeleted: true, ct);
        if (entry is null) return (null, ($"There is no template called '{key}'.", ErrorCodes.NotFound));
        if (!entry.IsCustom)
        {
            return (null, (
                $"\"{entry.Definition.Name}\" is sent by the {entry.Definition.Service} service when it happens, not to an audience.",
                ErrorCodes.InvalidState));
        }
        if (entry.IsDeleted) return (null, ("This template is deleted. Restore it first.", ErrorCodes.InvalidState));
        var english = await _unitOfWork.EmailContentVariantRepository.GetPublishedAsync(key, EmailLocales.Default, ct);
        if (english is null)
            return (null, ("Publish the English version first: it is what everyone without their own language is sent.", ErrorCodes.InvalidState));
        return (entry, null);
    }

    private static (Dictionary<string, string> Values, string? Error) ValidateValues(
        EmailDefinitionEntry entry, IReadOnlyDictionary<string, string>? supplied)
    {
        var declared = CustomEmailDefinitions.ReadVariables(entry.Custom!.Variables);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in supplied ?? new Dictionary<string, string>())
        {
            if (CustomEmailDefinitions.IsImplicit(name)) continue;
            if (!declared.Any(v => v.Name == name)) return (values, $"{{{{{name}}}}} is not a variable of this template.");
        }
        foreach (var variable in declared)
        {
            var value = (supplied is not null && supplied.TryGetValue(variable.Name, out var given) ? given : variable.Sample) ?? string.Empty;
            value = value.Trim();
            if (variable.Required && value.Length == 0) return (values, $"Fill in {variable.Label}.");
            if (value.Length > EmailCmsConstants.MaxSampleValueLength) return (values, $"{variable.Label} is too long.");
            if (value.Length > 0)
            {
                switch (variable.Type)
                {
                    case EmailCmsConstants.VariableUrl when !(Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)):
                        return (values, $"{variable.Label} must be a full https:// link.");
                    case EmailCmsConstants.VariableNumber when !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _):
                        return (values, $"{variable.Label} must be a number.");
                    case EmailCmsConstants.VariableDate when !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _):
                        return (values, $"{variable.Label} must be a date.");
                }
            }
            values[variable.Name] = value;
        }
        return (values, null);
    }

    private async Task<IReadOnlySet<Guid>> OptedOutAsync(EmailDefinitionEntry entry, IReadOnlyList<EmailAudienceMember> members, CancellationToken ct)
    {
        var type = entry.Custom?.Category switch
        {
            EmailCmsConstants.CategoryMarketing => NotificationConstants.TypePromotion,
            EmailCmsConstants.CategoryAnnouncement => NotificationConstants.TypeAnnouncement,
            _ => null,
        };
        if (type is null || members.Count == 0) return new HashSet<Guid>();
        return await _unitOfWork.NotificationPreferenceRepository.ListEmailOptOutsAsync(members.Select(m => m.UserId).ToList(), type, ct);
    }

    private async Task<HashSet<string>> PublishedLocalesAsync(string key, CancellationToken ct)
    {
        var variants = await _unitOfWork.EmailContentVariantRepository.ListAsync(key, ct);
        return variants
            .Where(v => v.Status == EmailCmsConstants.StatusActive && v.PublishedVersion > 0)
            .Select(v => v.Locale)
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task AddRecipientsAsync(
        EmailCampaign campaign, IReadOnlyList<EmailAudienceMember> members, IReadOnlySet<Guid> optedOut, EmailDefinitionEntry entry, CancellationToken ct)
    {
        var reason = entry.Custom?.Category == EmailCmsConstants.CategoryMarketing
            ? "Turned off promotional email."
            : "Turned off announcement email.";
        var rows = members.Select(member => new EmailCampaignRecipient
        {
            Id = Guid.CreateVersion7(),
            CampaignId = campaign.Id,
            UserId = member.UserId,
            Email = member.Email,
            FullName = member.FullName,
            Locale = member.Locale,
            Status = optedOut.Contains(member.UserId) ? EmailCmsConstants.RecipientSkipped : EmailCmsConstants.RecipientPending,
            Error = optedOut.Contains(member.UserId) ? reason : null,
        }).ToList();
        await _unitOfWork.EmailCampaignRecipientRepository.AddRangeAsync(rows, ct);
        campaign.TotalCount = rows.Count;
        campaign.SkippedCount = rows.Count(r => r.Status == EmailCmsConstants.RecipientSkipped);
    }

    private async Task CancelAsync(EmailCampaign campaign, Guid actorId, string reason, CancellationToken ct)
    {
        var skipped = await _unitOfWork.EmailCampaignRecipientRepository.SkipPendingAsync(campaign.Id, reason, ct);
        campaign.Status = EmailCmsConstants.CampaignCancelled;
        campaign.CancelledAt = Now;
        campaign.CancelledBy = actorId;
        campaign.CompletedAt = Now;
        campaign.SkippedCount += skipped;
    }

    private async Task FailAsync(EmailCampaign campaign, string error, CancellationToken ct)
    {
        _logger.LogWarning("Send {CampaignId} of {TemplateKey} failed: {Error}", campaign.Id, campaign.TemplateKey, error);
        var skipped = await _unitOfWork.EmailCampaignRecipientRepository.SkipPendingAsync(campaign.Id, error, ct);
        campaign.Status = EmailCmsConstants.CampaignFailed;
        campaign.Error = error.Length > 500 ? error[..500] : error;
        campaign.CompletedAt = Now;
        campaign.SkippedCount += skipped;
        await _unitOfWork.SaveChangesAsync();
    }

    private static RenderedEmail Render(EmailTemplateDefinition definition, StoredEmailTemplate stored, IReadOnlyDictionary<string, string> values)
    {
        var content = new EmailTemplateContent(stored.Subject, stored.Heading, stored.BodyHtml, stored.Preheader, stored.TextBody);
        var layout = string.IsNullOrWhiteSpace(stored.LayoutHtml) ? null : new EmailLayout(stored.LayoutHtml, stored.LayoutText, stored.LayoutDarkCss);
        if (layout is not null && EmailTemplateRenderer.ValidateLayout(layout).Count > 0) layout = null;
        return EmailTemplateRenderer.Render(definition, content, values, layout);
    }

    private static EmailCampaign NewCampaign(
        string key, string source, Guid? announcementId, EmailAudienceSpec spec, IReadOnlyDictionary<string, string> values,
        DateTime scheduledAt, Guid actorId, DateTime now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TemplateKey = key,
            Source = source,
            AnnouncementId = announcementId,
            Audience = JsonSerializer.Serialize(spec.ToDto(), Json),
            Values = JsonSerializer.Serialize(values, Json),
            Status = EmailCmsConstants.CampaignQueued,
            ScheduledAt = scheduledAt,
            CreatedBy = actorId,
            CreatedAt = now,
        };

    private static Dictionary<string, string> ReadValues(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Markdown reduced to readable text for an email variable.</summary>
    internal static string PlainText(string markdown)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(markdown ?? string.Empty, @"!\[[^\]]*\]\([^)]*\)", string.Empty);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[*_`>#]+", string.Empty);
        return text.Trim();
    }

    private static EmailCampaignDto ToDto(EmailCampaign c, string templateName)
    {
        EmailAudienceDto audience;
        try
        {
            audience = JsonSerializer.Deserialize<EmailAudienceDto>(c.Audience, Json) ?? new EmailAudienceDto("ALL", [], [], [], [], null);
        }
        catch (JsonException)
        {
            audience = new EmailAudienceDto("ALL", [], [], [], [], null);
        }
        return new EmailCampaignDto(
            c.Id, c.TemplateKey, templateName, c.Source, c.AnnouncementId, audience, ReadValues(c.Values), c.Status,
            c.ScheduledAt, c.StartedAt, c.CompletedAt, c.TotalCount, c.SentCount, c.FailedCount, c.SkippedCount, c.Error,
            c.CreatedBy, c.CreatedAt, c.CancelledBy, c.CancelledAt);
    }
}
