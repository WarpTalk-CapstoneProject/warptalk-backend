using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WarpTalk.NotificationService.Application.DTOs.Common;
using WarpTalk.NotificationService.Application.DTOs.EmailTemplates;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Email;

namespace WarpTalk.NotificationService.Application.Services.EmailCms;

public interface IEmailBlockService
{
    Task<Result<IReadOnlyList<EmailBlockDto>>> ListAsync(string? kind, CancellationToken ct = default);
    Task<Result<EmailBlockDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<EmailBlockDto>> CreateAsync(AdminActorContext actor, CreateEmailBlockRequest request, CancellationToken ct = default);
    Task<Result<EmailBlockDto>> SaveDraftAsync(AdminActorContext actor, Guid id, SaveEmailBlockDraftRequest request, CancellationToken ct = default);
    Task<Result<EmailBlockDto>> PublishAsync(AdminActorContext actor, Guid id, PublishEmailRequest request, CancellationToken ct = default);
    Task<Result<EmailBlockDto>> DiscardDraftAsync(AdminActorContext actor, Guid id, CancellationToken ct = default);
    Task<Result<EmailBlockDto>> DuplicateAsync(AdminActorContext actor, Guid id, DuplicateEmailBlockRequest request, CancellationToken ct = default);
    Task<Result<EmailBlockDto>> ArchiveAsync(AdminActorContext actor, Guid id, CancellationToken ct = default);
    Task<Result<EmailBlockDto>> UnarchiveAsync(AdminActorContext actor, Guid id, CancellationToken ct = default);
    Task<Result> DeleteAsync(AdminActorContext actor, Guid id, CancellationToken ct = default);
    Task<Result<EmailBlockDto>> SetDefaultAsync(AdminActorContext actor, Guid id, CancellationToken ct = default);
    Task<Result<IReadOnlyList<EmailCmsVersionDto>>> ListVersionsAsync(Guid id, CancellationToken ct = default);
    Task<Result<EmailBlockDto>> RestoreVersionAsync(AdminActorContext actor, Guid id, int version, CancellationToken ct = default);
    Task<Result<EmailPreviewDto>> PreviewAsync(Guid? id, string kind, EmailBlockPreviewRequest request, CancellationToken ct = default);

    /// <summary>A stored block rendered inside an email (published side unless <paramref name="draft"/>): its thumbnail and preview.</summary>
    Task<Result<EmailPreviewDto>> RenderAsync(Guid id, bool dark, string? templateKey, string? locale, bool draft, CancellationToken ct = default);
    Task<Result<BulkResultDto>> BulkAsync(AdminActorContext actor, EmailBlockBulkRequest request, CancellationToken ct = default);
}

/// <summary>
/// Layouts and blocks: the reusable design every email's content is rendered with, managed apart
/// from any one email's wording.
///
/// A publish here changes every email that uses the block, so it is checked the way an email
/// publish is: a layout must still have exactly one {{content}} slot, and a block is expanded into
/// every published email that includes it and each of those must still validate. Archiving or
/// deleting a block something still uses is refused rather than letting sends fall back silently.
/// </summary>
public sealed partial class EmailBlockService : IEmailBlockService
{
    private const int MaxVersionsListed = 50;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{1,59}$")]
    private static partial Regex KeyPattern();

    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;
    private readonly ILogger<EmailBlockService> _logger;

    public EmailBlockService(
        IUnitOfWork unitOfWork,
        ILogger<EmailBlockService> logger,
        TimeProvider? timeProvider = null,
        IEmailDefinitionProvider? definitions = null)
    {
        _unitOfWork = unitOfWork;
        _definitions = definitions ?? new EmailDefinitionProvider(unitOfWork);
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    private readonly IEmailDefinitionProvider _definitions;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<Result<IReadOnlyList<EmailBlockDto>>> ListAsync(string? kind, CancellationToken ct = default)
    {
        var normalizedKind = NormalizeKind(kind);
        if (kind is not null && normalizedKind is null)
            return Result.Failure<IReadOnlyList<EmailBlockDto>>("Kind must be LAYOUT or PARTIAL.", ErrorCodes.ValidationError);

        var blocks = await _unitOfWork.EmailBlockRepository.ListAsync(normalizedKind, ct);
        var usage = await UsageAsync(ct);
        IReadOnlyList<EmailBlockDto> items = blocks.Select(block => ToDto(block, usage)).ToList();
        return Result.Success(items);
    }

    public async Task<Result<EmailBlockDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var block = await _unitOfWork.EmailBlockRepository.GetByIdAsync(id, ct);
        if (block is null) return NotFound<EmailBlockDto>();
        return Result.Success(ToDto(block, await UsageAsync(ct)));
    }

    public async Task<Result<EmailBlockDto>> CreateAsync(AdminActorContext actor, CreateEmailBlockRequest request, CancellationToken ct = default)
    {
        var kind = NormalizeKind(request.Kind);
        if (kind is null) return Result.Failure<EmailBlockDto>("Kind must be LAYOUT or PARTIAL.", ErrorCodes.ValidationError);

        var key = (request.Key ?? string.Empty).Trim().ToLowerInvariant();
        var keyError = KeyError(key);
        if (keyError is not null) return Result.Failure<EmailBlockDto>(keyError, ErrorCodes.ValidationError);
        if (await _unitOfWork.EmailBlockRepository.KeyExistsAsync(kind, key, null, ct))
            return Result.Failure<EmailBlockDto>($"A {KindLabel(kind)} called '{key}' already exists.", ErrorCodes.Conflict);

        var fieldsError = FieldsError(request.Name, request.Description, request.Html, request.Text, request.DarkCss);
        if (fieldsError is not null) return Result.Failure<EmailBlockDto>(fieldsError, ErrorCodes.ValidationError);

        var now = Now;
        var block = new EmailBlock
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            Key = key,
            Status = EmailCmsConstants.StatusActive,
            CreatedAt = now,
            CreatedBy = actor.ActorId,
        };
        ApplyDraft(block, request.Name, request.Description, request.Html, request.Text, request.DarkCss, actor.ActorId, now);

        await _unitOfWork.EmailBlockRepository.AddAsync(block, ct);
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(ToDto(block, await UsageAsync(ct)));
    }

    public async Task<Result<EmailBlockDto>> SaveDraftAsync(AdminActorContext actor, Guid id, SaveEmailBlockDraftRequest request, CancellationToken ct = default)
    {
        var block = await _unitOfWork.EmailBlockRepository.GetByIdAsync(id, ct);
        if (block is null) return NotFound<EmailBlockDto>();
        if (block.Status == EmailCmsConstants.StatusArchived)
            return Result.Failure<EmailBlockDto>("This block is archived. Restore it before editing.", ErrorCodes.InvalidState);
        if (request.ExpectedDraftUpdatedAt is { } expected
            && Math.Abs((expected.ToUniversalTime() - block.DraftUpdatedAt).TotalMilliseconds) >= 1)
        {
            return Result.Failure<EmailBlockDto>(
                "Someone saved this draft since you opened it. Reload to see their change before saving yours.", ErrorCodes.Conflict);
        }

        var fieldsError = FieldsError(request.Name, request.Description, request.Html, request.Text, request.DarkCss);
        if (fieldsError is not null) return Result.Failure<EmailBlockDto>(fieldsError, ErrorCodes.ValidationError);

        ApplyDraft(block, request.Name, request.Description, request.Html, request.Text, request.DarkCss, actor.ActorId, Now);
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(ToDto(block, await UsageAsync(ct)));
    }

    public async Task<Result<EmailBlockDto>> PublishAsync(AdminActorContext actor, Guid id, PublishEmailRequest request, CancellationToken ct = default)
    {
        var block = await _unitOfWork.EmailBlockRepository.GetByIdAsync(id, ct);
        if (block is null) return NotFound<EmailBlockDto>();
        if (block.Status == EmailCmsConstants.StatusArchived)
            return Result.Failure<EmailBlockDto>("This block is archived. Restore it before publishing.", ErrorCodes.InvalidState);
        if (request.ExpectedPublishedVersion is { } expected && expected != block.PublishedVersion)
            return Result.Failure<EmailBlockDto>($"Someone published this block since you opened it (now v{block.PublishedVersion}).", ErrorCodes.Conflict);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > EmailCmsConstants.MaxNoteLength })
            return Result.Failure<EmailBlockDto>($"The note must be {EmailCmsConstants.MaxNoteLength} characters or fewer.", ErrorCodes.ValidationError);

        var publishError = await PublishErrorAsync(block, ct);
        if (publishError is not null) return Result.Failure<EmailBlockDto>(publishError, ErrorCodes.ValidationError);

        var now = Now;
        block.PublishedHtml = block.DraftHtml;
        block.PublishedText = block.DraftText;
        block.PublishedDarkCss = block.DraftDarkCss;
        block.PublishedVersion += 1;
        block.PublishedAt = now;
        block.PublishedBy = actor.ActorId;
        await _unitOfWork.EmailCmsVersionRepository.AddAsync(new EmailCmsVersion
        {
            Id = Guid.CreateVersion7(),
            OwnerType = EmailCmsConstants.OwnerBlock,
            OwnerId = block.Id,
            Version = block.PublishedVersion,
            Action = EmailCmsConstants.ActionPublished,
            Snapshot = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["html"] = block.PublishedHtml,
                ["text"] = block.PublishedText,
                ["darkCss"] = block.PublishedDarkCss,
            }),
            Note = note,
            CreatedBy = actor.ActorId,
            CreatedAt = now,
        }, ct);

        // The first layout anyone publishes becomes the default, so it is used at all.
        if (block.Kind == EmailCmsConstants.KindLayout && !block.IsDefault)
        {
            var defaults = await _unitOfWork.EmailBlockRepository.GetDefaultLayoutsAsync(ct);
            if (defaults.Count == 0) block.IsDefault = true;
        }

        await _unitOfWork.SaveChangesAsync();
        _logger.LogInformation("Email {Kind} {Key} published v{Version}.", block.Kind, block.Key, block.PublishedVersion);
        return Result.Success(ToDto(block, await UsageAsync(ct)));
    }

    public async Task<Result<EmailBlockDto>> DiscardDraftAsync(AdminActorContext actor, Guid id, CancellationToken ct = default)
    {
        var block = await _unitOfWork.EmailBlockRepository.GetByIdAsync(id, ct);
        if (block is null) return NotFound<EmailBlockDto>();
        if (block.PublishedHtml is null)
            return Result.Failure<EmailBlockDto>("It has never been published, so there is nothing to go back to. Delete it instead.", ErrorCodes.InvalidState);

        block.DraftHtml = block.PublishedHtml;
        block.DraftText = block.PublishedText;
        block.DraftDarkCss = block.PublishedDarkCss;
        block.DraftUpdatedAt = Now;
        block.DraftUpdatedBy = actor.ActorId;
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(ToDto(block, await UsageAsync(ct)));
    }

    public async Task<Result<EmailBlockDto>> DuplicateAsync(AdminActorContext actor, Guid id, DuplicateEmailBlockRequest request, CancellationToken ct = default)
    {
        var source = await _unitOfWork.EmailBlockRepository.GetByIdAsync(id, ct);
        if (source is null) return NotFound<EmailBlockDto>();
        var created = await CreateAsync(actor, new CreateEmailBlockRequest(
            source.Kind,
            request.Key,
            string.IsNullOrWhiteSpace(request.Name) ? $"Copy of {source.Name}" : request.Name,
            source.Description,
            source.DraftHtml,
            source.DraftText,
            source.DraftDarkCss), ct);
        if (created.IsSuccess)
        {
        }
        return created;
    }

    public async Task<Result<EmailBlockDto>> ArchiveAsync(AdminActorContext actor, Guid id, CancellationToken ct = default)
    {
        var block = await _unitOfWork.EmailBlockRepository.GetByIdAsync(id, ct);
        if (block is null) return NotFound<EmailBlockDto>();
        if (block.Status == EmailCmsConstants.StatusArchived)
            return Result.Failure<EmailBlockDto>("It is already archived.", ErrorCodes.InvalidState);

        var usage = await UsageAsync(ct);
        var users = usage.GetValueOrDefault(block.Id) ?? [];
        if (users.Count > 0)
            return Result.Failure<EmailBlockDto>($"\"{block.Name}\" is still used by {string.Join(", ", users.Take(5))}. Move those off it first.", ErrorCodes.InvalidState);
        if (block.IsDefault)
            return Result.Failure<EmailBlockDto>("This is the default layout. Make another layout the default first.", ErrorCodes.InvalidState);

        block.Status = EmailCmsConstants.StatusArchived;
        block.ArchivedAt = Now;
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(ToDto(block, usage));
    }

    public async Task<Result<EmailBlockDto>> UnarchiveAsync(AdminActorContext actor, Guid id, CancellationToken ct = default)
    {
        var block = await _unitOfWork.EmailBlockRepository.GetByIdAsync(id, ct);
        if (block is null) return NotFound<EmailBlockDto>();
        if (block.Status != EmailCmsConstants.StatusArchived)
            return Result.Failure<EmailBlockDto>("It is not archived.", ErrorCodes.InvalidState);

        block.Status = EmailCmsConstants.StatusActive;
        block.ArchivedAt = null;
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(ToDto(block, await UsageAsync(ct)));
    }

    public async Task<Result> DeleteAsync(AdminActorContext actor, Guid id, CancellationToken ct = default)
    {
        var block = await _unitOfWork.EmailBlockRepository.GetByIdAsync(id, ct);
        if (block is null) return Result.Failure("Block not found.", ErrorCodes.NotFound);
        if (block.PublishedVersion > 0)
            return Result.Failure("Only a block that was never published can be deleted. Archive this one instead, so its history stays.", ErrorCodes.InvalidState);
        var users = (await UsageAsync(ct)).GetValueOrDefault(block.Id) ?? [];
        if (users.Count > 0)
            return Result.Failure($"\"{block.Name}\" is still used by {string.Join(", ", users.Take(5))}.", ErrorCodes.InvalidState);

        _unitOfWork.EmailBlockRepository.Remove(block);
        await _unitOfWork.SaveChangesAsync();
        return Result.Success();
    }

    public async Task<Result<EmailBlockDto>> SetDefaultAsync(AdminActorContext actor, Guid id, CancellationToken ct = default)
    {
        var block = await _unitOfWork.EmailBlockRepository.GetByIdAsync(id, ct);
        if (block is null) return NotFound<EmailBlockDto>();
        if (block.Kind != EmailCmsConstants.KindLayout)
            return Result.Failure<EmailBlockDto>("Only a layout can be the default.", ErrorCodes.ValidationError);
        if (block.Status == EmailCmsConstants.StatusArchived || block.PublishedHtml is null)
            return Result.Failure<EmailBlockDto>("Publish the layout before making it the default: senders only use published layouts.", ErrorCodes.InvalidState);

        foreach (var other in await _unitOfWork.EmailBlockRepository.GetDefaultLayoutsAsync(ct))
            other.IsDefault = false;
        block.IsDefault = true;
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(ToDto(block, await UsageAsync(ct)));
    }

    public async Task<Result<IReadOnlyList<EmailCmsVersionDto>>> ListVersionsAsync(Guid id, CancellationToken ct = default)
    {
        if (await _unitOfWork.EmailBlockRepository.GetByIdAsync(id, ct) is null) return NotFound<IReadOnlyList<EmailCmsVersionDto>>();
        var versions = await _unitOfWork.EmailCmsVersionRepository.ListAsync(EmailCmsConstants.OwnerBlock, id, MaxVersionsListed, ct);
        IReadOnlyList<EmailCmsVersionDto> items = versions
            .Select(v => new EmailCmsVersionDto(v.Version, v.Action, EmailContentService.ReadSnapshot(v.Snapshot), v.Note, v.CreatedBy, v.CreatedAt))
            .ToList();
        return Result.Success(items);
    }

    public async Task<Result<EmailBlockDto>> RestoreVersionAsync(AdminActorContext actor, Guid id, int version, CancellationToken ct = default)
    {
        var block = await _unitOfWork.EmailBlockRepository.GetByIdAsync(id, ct);
        if (block is null) return NotFound<EmailBlockDto>();
        var entry = await _unitOfWork.EmailCmsVersionRepository.GetAsync(EmailCmsConstants.OwnerBlock, id, version, ct);
        if (entry is null) return Result.Failure<EmailBlockDto>($"Version {version} does not exist.", ErrorCodes.NotFound);

        var fields = EmailContentService.ReadSnapshot(entry.Snapshot);
        block.DraftHtml = fields.GetValueOrDefault("html") ?? block.DraftHtml;
        block.DraftText = fields.GetValueOrDefault("text");
        block.DraftDarkCss = fields.GetValueOrDefault("darkCss");
        block.DraftUpdatedAt = Now;
        block.DraftUpdatedBy = actor.ActorId;
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(ToDto(block, await UsageAsync(ct)));
    }

    public async Task<Result<EmailPreviewDto>> RenderAsync(
        Guid id, bool dark, string? templateKey, string? locale, bool draft, CancellationToken ct = default)
    {
        var block = await _unitOfWork.EmailBlockRepository.GetByIdAsync(id, ct);
        if (block is null) return NotFound<EmailPreviewDto>();
        var usePublished = !draft && block.PublishedHtml is not null;
        return await PreviewAsync(
            id,
            block.Kind,
            new EmailBlockPreviewRequest(
                usePublished ? block.PublishedHtml! : block.DraftHtml,
                usePublished ? block.PublishedText : block.DraftText,
                usePublished ? block.PublishedDarkCss : block.DraftDarkCss,
                templateKey,
                locale,
                dark),
            ct);
    }

    public async Task<Result<EmailPreviewDto>> PreviewAsync(Guid? id, string kind, EmailBlockPreviewRequest request, CancellationToken ct = default)
    {
        var normalizedKind = NormalizeKind(kind);
        if (normalizedKind is null) return Result.Failure<EmailPreviewDto>("Kind must be LAYOUT or PARTIAL.", ErrorCodes.ValidationError);

        var definition = (request.TemplateKey is null ? null : (await _definitions.FindAsync(request.TemplateKey, includeDeleted: true, ct))?.Definition)
            ?? EmailTemplateCatalog.All[0];
        var resolver = new EmailPublishedResolver(_unitOfWork);
        var stored = await resolver.FindActiveAsync(definition.Key, request.Locale, ct);
        var content = stored is null
            ? definition.Default
            : new EmailTemplateContent(stored.Subject, stored.Heading, stored.BodyHtml, stored.Preheader, stored.TextBody);
        var partials = await _unitOfWork.EmailBlockRepository.GetPublishedPartialsAsync(ct);

        List<EmailTemplateIssue> issues;
        EmailLayout? layout;
        var name = "Built-in layout";
        if (normalizedKind == EmailCmsConstants.KindLayout)
        {
            layout = new EmailLayout(
                EmailTemplateRenderer.ExpandPartials(request.Html, partials),
                string.IsNullOrWhiteSpace(request.Text) ? null : request.Text,
                string.IsNullOrWhiteSpace(request.DarkCss) ? null : request.DarkCss);
            issues = EmailTemplateRenderer.ValidateLayout(layout).ToList();
            name = "This layout";
        }
        else
        {
            // A block is previewed on its own, inside the email's published layout.
            layout = stored?.LayoutHtml is null ? null : new EmailLayout(stored.LayoutHtml, stored.LayoutText, stored.LayoutDarkCss);
            content = content with { BodyHtml = request.Html ?? string.Empty };
            issues = EmailTemplateRenderer.ValidatePartial(request.Html).ToList();
        }

        var values = EmailTemplateCatalog.SampleValues(definition);
        var rendered = EmailTemplateRenderer.Render(definition, content, values, layout, new EmailRenderOptions(request.Dark));
        return Result.Success(new EmailPreviewDto(
            rendered.Subject,
            EmailTemplateRenderer.RenderPlainLine(content.Preheader, values),
            rendered.HtmlBody,
            rendered.TextBody,
            issues,
            name));
    }

    public async Task<Result<BulkResultDto>> BulkAsync(AdminActorContext actor, EmailBlockBulkRequest request, CancellationToken ct = default)
    {
        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("publish" or "archive" or "delete" or "duplicate"))
            return Result.Failure<BulkResultDto>("Action must be publish, archive, delete or duplicate.", ErrorCodes.ValidationError);
        var ids = (request.Ids ?? []).Distinct().ToList();
        if (ids.Count == 0) return Result.Failure<BulkResultDto>("Select at least one block.", ErrorCodes.ValidationError);
        if (ids.Count > AnnouncementConstants.MaxBulkItems)
            return Result.Failure<BulkResultDto>("Too many blocks in one action.", ErrorCodes.ValidationError);

        var results = new List<BulkItemResultDto>();
        foreach (var id in ids)
        {
            Result outcome = action switch
            {
                "publish" => await PublishAsync(actor, id, new PublishEmailRequest(), ct),
                "archive" => await ArchiveAsync(actor, id, ct),
                "delete" => await DeleteAsync(actor, id, ct),
                _ => await DuplicateNextKeyAsync(actor, id, ct),
            };
            results.Add(new BulkItemResultDto(id.ToString(), outcome.IsSuccess, outcome.Error));
        }
        return Result.Success(new BulkResultDto(results));
    }

    private async Task<Result> DuplicateNextKeyAsync(AdminActorContext actor, Guid id, CancellationToken ct)
    {
        var source = await _unitOfWork.EmailBlockRepository.GetByIdAsync(id, ct);
        if (source is null) return Result.Failure("Block not found.", ErrorCodes.NotFound);
        for (var n = 2; n < 100; n++)
        {
            var suffix = $"-copy{(n == 2 ? string.Empty : n.ToString())}";
            var key = source.Key.Length + suffix.Length > EmailCmsConstants.MaxKeyLength
                ? source.Key[..(EmailCmsConstants.MaxKeyLength - suffix.Length)] + suffix
                : source.Key + suffix;
            if (!await _unitOfWork.EmailBlockRepository.KeyExistsAsync(source.Kind, key, null, ct))
                return await DuplicateAsync(actor, id, new DuplicateEmailBlockRequest(key, $"Copy of {source.Name}"), ct);
        }
        return Result.Failure("Could not find a free key for the copy.", ErrorCodes.Conflict);
    }

    /// <summary>Why the draft cannot be published — including every published email it would break.</summary>
    private async Task<string?> PublishErrorAsync(EmailBlock block, CancellationToken ct)
    {
        var partials = new Dictionary<string, string>(await _unitOfWork.EmailBlockRepository.GetPublishedPartialsAsync(ct), StringComparer.Ordinal);

        if (block.Kind == EmailCmsConstants.KindLayout)
        {
            var layout = new EmailLayout(EmailTemplateRenderer.ExpandPartials(block.DraftHtml, partials), block.DraftText, block.DraftDarkCss);
            var issues = EmailTemplateRenderer.ValidateLayout(layout);
            return issues.Count > 0 ? issues[0].Message : null;
        }

        var partialIssues = EmailTemplateRenderer.ValidatePartial(block.DraftHtml);
        if (partialIssues.Count > 0) return partialIssues[0].Message;

        partials[block.Key] = block.DraftHtml;
        var variants = await _unitOfWork.EmailContentVariantRepository.ListAsync(null, ct);
        var include = $"{{{{> {block.Key}}}}}";
        foreach (var variant in variants.Where(v => v.Status == EmailCmsConstants.StatusActive && v.PublishedVersion > 0))
        {
            var fields = new[] { variant.PublishedSubject, variant.PublishedPreheader, variant.PublishedHeading, variant.PublishedBodyHtml, variant.PublishedTextBody };
            if (!fields.Any(field => field is not null && EmailTemplateRenderer.ReferencedPartials(field).Contains(block.Key))) continue;

            var definition = (await _definitions.FindAsync(variant.TemplateKey, includeDeleted: false, ct))?.Definition;
            if (definition is null) continue;
            var content = new EmailTemplateContent(
                EmailTemplateRenderer.ExpandPartials(variant.PublishedSubject, partials),
                EmailTemplateRenderer.ExpandPartials(variant.PublishedHeading, partials),
                EmailTemplateRenderer.ExpandPartials(variant.PublishedBodyHtml, partials),
                EmailTemplateRenderer.ExpandPartials(variant.PublishedPreheader, partials),
                variant.PublishedTextBody is null ? null : EmailTemplateRenderer.ExpandPartials(variant.PublishedTextBody, partials));
            var issues = EmailTemplateRenderer.Validate(definition, content);
            if (issues.Count > 0)
                return $"Publishing {include} would break \"{definition.Name}\" ({variant.Locale}): {issues[0].Message}";
        }
        return null;
    }

    /// <summary>Block id → what uses it: emails (by layout choice or {{&gt; key}}), and layouts including a partial.</summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> UsageAsync(CancellationToken ct)
    {
        var blocks = await _unitOfWork.EmailBlockRepository.ListAsync(null, ct);
        var variants = (await _unitOfWork.EmailContentVariantRepository.ListAsync(null, ct))
            .Where(v => v.Status == EmailCmsConstants.StatusActive)
            .ToList();
        var usage = new Dictionary<Guid, IReadOnlyList<string>>();

        foreach (var block in blocks)
        {
            var users = new List<string>();
            if (block.Kind == EmailCmsConstants.KindLayout)
            {
                users.AddRange(variants
                    .Where(v => v.DraftLayoutId == block.Id || v.PublishedLayoutId == block.Id)
                    .Select(v => $"{v.TemplateKey} ({v.Locale})"));
            }
            else
            {
                bool Includes(string? text) => text is not null && EmailTemplateRenderer.ReferencedPartials(text).Contains(block.Key);
                users.AddRange(variants
                    .Where(v => Includes(v.DraftBodyHtml) || Includes(v.PublishedBodyHtml) || Includes(v.DraftHeading)
                                || Includes(v.PublishedHeading) || Includes(v.DraftSubject) || Includes(v.PublishedSubject))
                    .Select(v => $"{v.TemplateKey} ({v.Locale})"));
                users.AddRange(blocks
                    .Where(b => b.Kind == EmailCmsConstants.KindLayout && b.Status == EmailCmsConstants.StatusActive
                                && (Includes(b.DraftHtml) || Includes(b.PublishedHtml)))
                    .Select(b => $"layout: {b.Name}"));
            }
            usage[block.Id] = users.Distinct().ToList();
        }
        return usage;
    }

    private static EmailBlockDto ToDto(EmailBlock b, IReadOnlyDictionary<Guid, IReadOnlyList<string>> usage) =>
        new(
            b.Id,
            b.Kind,
            b.Key,
            b.Name,
            b.Description,
            b.Status,
            b.IsDefault,
            new EmailBlockFieldsDto(b.DraftHtml, b.DraftText, b.DraftDarkCss),
            b.PublishedHtml is null ? null : new EmailBlockFieldsDto(b.PublishedHtml, b.PublishedText, b.PublishedDarkCss),
            b.PublishedVersion,
            b.PublishedHtml is null || b.DraftHtml != b.PublishedHtml || b.DraftText != b.PublishedText || b.DraftDarkCss != b.PublishedDarkCss,
            b.PublishedAt,
            b.PublishedBy,
            b.DraftUpdatedAt,
            b.DraftUpdatedBy,
            b.ArchivedAt,
            b.CreatedAt,
            usage.GetValueOrDefault(b.Id) ?? []);

    private static void ApplyDraft(EmailBlock block, string name, string? description, string html, string? text, string? darkCss, Guid actorId, DateTime now)
    {
        block.Name = name.Trim();
        block.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        block.DraftHtml = html ?? string.Empty;
        var layout = block.Kind == EmailCmsConstants.KindLayout;
        block.DraftText = layout && !string.IsNullOrWhiteSpace(text) ? text : null;
        block.DraftDarkCss = layout && !string.IsNullOrWhiteSpace(darkCss) ? darkCss.Trim() : null;
        block.DraftUpdatedAt = now;
        block.DraftUpdatedBy = actorId;
    }

    private static string? FieldsError(string? name, string? description, string? html, string? text, string? darkCss)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "A name is required.";
        if (trimmed.Length > EmailCmsConstants.MaxNameLength) return $"The name must be {EmailCmsConstants.MaxNameLength} characters or fewer.";
        if ((description ?? string.Empty).Length > EmailCmsConstants.MaxDescriptionLength) return "The description is too long.";
        if ((html ?? string.Empty).Length > EmailTemplateRenderer.MaxBodyLength) return "The HTML is too long.";
        if ((text ?? string.Empty).Length > EmailTemplateRenderer.MaxTextLength) return "The plain-text wrapper is too long.";
        if ((darkCss ?? string.Empty).Length > EmailTemplateRenderer.MaxTextLength) return "The dark-mode CSS is too long.";
        return null;
    }

    private static string? KeyError(string key) =>
        KeyPattern().IsMatch(key)
            ? null
            : "The key must be 2–60 characters of lower-case letters, digits, '-' or '_', starting with a letter or digit.";

    private static string? NormalizeKind(string? kind)
    {
        var upper = (kind ?? string.Empty).Trim().ToUpperInvariant();
        return EmailCmsConstants.Kinds.Contains(upper) ? upper : null;
    }

    private static string KindLabel(string kind) => kind == EmailCmsConstants.KindLayout ? "layout" : "block";

    private static Result<T> NotFound<T>() => Result.Failure<T>("Block not found.", ErrorCodes.NotFound);
}
