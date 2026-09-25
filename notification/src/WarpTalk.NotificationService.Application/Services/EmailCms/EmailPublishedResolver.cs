using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared.Email;

namespace WarpTalk.NotificationService.Application.Services.EmailCms;

/// <summary>
/// What a send reads: the PUBLISHED content for a key and locale (falling back to English), its
/// published layout, and every published partial expanded. Never a draft — editing a draft must
/// not change what is sent until it is published.
///
/// This is the notification service's own <see cref="IEmailTemplateSource"/>, and what the
/// GetEmailTemplate RPC answers every other sender with.
/// </summary>
public sealed class EmailPublishedResolver : IEmailTemplateSource
{
    private readonly IUnitOfWork _unitOfWork;

    public EmailPublishedResolver(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<StoredEmailTemplate?> FindActiveAsync(string templateKey, string? locale, CancellationToken ct = default)
    {
        if (EmailTemplateCatalog.Find(templateKey) is null) return null;

        EmailContentVariant? variant = null;
        foreach (var candidate in EmailLocales.FallbackChain(locale))
        {
            variant = await _unitOfWork.EmailContentVariantRepository.GetPublishedAsync(templateKey, candidate, ct);
            if (variant is not null) break;
        }

        var partials = await _unitOfWork.EmailBlockRepository.GetPublishedPartialsAsync(ct);
        var layout = await ResolvePublishedLayoutAsync(variant?.PublishedLayoutId, ct);

        if (variant is null)
        {
            // No published content in any locale, but an admin may still have published a default
            // layout: the built-in wording goes out inside it.
            if (layout is null) return null;
            var definition = EmailTemplateCatalog.Get(templateKey);
            return Stored(definition.Default, 0, EmailLocales.Default, layout, partials);
        }

        var content = new EmailTemplateContent(
            variant.PublishedSubject ?? string.Empty,
            variant.PublishedHeading ?? string.Empty,
            variant.PublishedBodyHtml ?? string.Empty,
            variant.PublishedPreheader ?? string.Empty,
            variant.PublishedTextBody);
        return Stored(content, variant.PublishedVersion, variant.Locale, layout, partials);
    }

    /// <summary>
    /// The layout a published variant renders into: its own if chosen, published and active;
    /// otherwise the default layout; otherwise null (the built-in layout).
    /// </summary>
    public async Task<EmailBlock?> ResolvePublishedLayoutAsync(Guid? layoutId, CancellationToken ct)
    {
        if (layoutId is { } id)
        {
            var chosen = await _unitOfWork.EmailBlockRepository.GetByIdAsync(id, ct);
            if (IsUsable(chosen)) return chosen;
        }

        var defaults = await _unitOfWork.EmailBlockRepository.GetDefaultLayoutsAsync(ct);
        return defaults.FirstOrDefault(IsUsable);
    }

    private static bool IsUsable(EmailBlock? block) =>
        block is { Kind: EmailCmsConstants.KindLayout, Status: EmailCmsConstants.StatusActive, PublishedHtml: not null };

    private static StoredEmailTemplate Stored(
        EmailTemplateContent content,
        int version,
        string locale,
        EmailBlock? layout,
        IReadOnlyDictionary<string, string> partials) =>
        new(
            EmailTemplateRenderer.ExpandPartials(content.Subject, partials),
            EmailTemplateRenderer.ExpandPartials(content.Heading, partials),
            EmailTemplateRenderer.ExpandPartials(content.BodyHtml, partials),
            version)
        {
            Preheader = EmailTemplateRenderer.ExpandPartials(content.Preheader, partials),
            TextBody = content.TextBody,
            LayoutHtml = layout is null ? null : EmailTemplateRenderer.ExpandPartials(layout.PublishedHtml, partials),
            LayoutText = layout?.PublishedText,
            LayoutDarkCss = layout?.PublishedDarkCss,
            Locale = locale,
        };
}
