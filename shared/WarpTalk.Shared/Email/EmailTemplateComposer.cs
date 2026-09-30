using Grpc.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WarpTalk.Shared.Protos;

namespace WarpTalk.Shared.Email;

/// <summary>Where a sender finds the published version of an email, if there is one.</summary>
public interface IEmailTemplateSource
{
    /// <summary>
    /// The published content for <paramref name="locale"/> (falling back to English), with its
    /// layout, or null to use the built-in wording. Drafts are never returned: editing a draft
    /// must not change what is sent until it is published.
    /// </summary>
    Task<StoredEmailTemplate?> FindActiveAsync(string templateKey, string? locale, CancellationToken ct = default);
}

/// <summary>
/// What every sender calls to build an email. Reads the published template first and falls back
/// to the catalog default — so an admin's published edit is what goes out, and an outage of the
/// store never stops a verification email from going out at all.
/// </summary>
public interface IEmailTemplateComposer
{
    /// <param name="locale">The recipient's language when the sender knows it; null means English.</param>
    Task<RenderedEmail> ComposeAsync(
        string templateKey,
        IReadOnlyDictionary<string, string> values,
        string? locale = null,
        CancellationToken ct = default);
}

/// <summary>
/// Counts what was actually handed to a provider, per template and locale, so the CMS can show
/// how often each email goes out and how often the provider refused it.
/// </summary>
public interface IEmailDeliveryRecorder
{
    /// <summary>Never throws: a counter must not fail an email that was already sent.</summary>
    Task RecordAsync(RenderedEmail email, bool succeeded, CancellationToken ct = default);
}

public sealed class EmailTemplateComposer : IEmailTemplateComposer
{
    private readonly IEmailTemplateSource _source;
    private readonly ILogger<EmailTemplateComposer> _logger;

    public EmailTemplateComposer(IEmailTemplateSource source, ILogger<EmailTemplateComposer>? logger = null)
    {
        _source = source;
        _logger = logger ?? NullLogger<EmailTemplateComposer>.Instance;
    }

    public async Task<RenderedEmail> ComposeAsync(
        string templateKey,
        IReadOnlyDictionary<string, string> values,
        string? locale = null,
        CancellationToken ct = default)
    {
        var definition = EmailTemplateCatalog.Get(templateKey);
        var content = definition.Default;
        EmailLayout? layout = null;
        var usedLocale = EmailLocales.Default;
        var version = 0;

        StoredEmailTemplate? stored = null;
        try
        {
            stored = await _source.FindActiveAsync(templateKey, EmailLocales.Normalize(locale), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail open to the built-in wording: a customised email that cannot be read is still
            // better sent plain than not sent, and "verify your email" must never wait on this.
            _logger.LogWarning(ex, "Could not read the stored {TemplateKey} email template; sending the default.", templateKey);
        }

        if (stored is not null)
        {
            var candidate = new EmailTemplateContent(
                stored.Subject, stored.Heading, stored.BodyHtml, stored.Preheader, stored.TextBody);
            var issues = EmailTemplateRenderer.Validate(definition, candidate);
            if (issues.Count == 0)
            {
                content = candidate;
                usedLocale = EmailLocales.Normalize(stored.Locale) ?? EmailLocales.Default;
                version = stored.Version;
            }
            else
            {
                // Published content is validated on publish, so this means the catalog moved under
                // a stored row (a variable was renamed or became required). Sending it would ship
                // a broken email; the default cannot be broken.
                _logger.LogWarning(
                    "Stored {TemplateKey} email template v{Version} no longer validates ({Issue}); sending the default.",
                    templateKey, stored.Version, issues[0].Message);
            }

            if (!string.IsNullOrWhiteSpace(stored.LayoutHtml))
            {
                var candidateLayout = new EmailLayout(stored.LayoutHtml, stored.LayoutText, stored.LayoutDarkCss);
                if (EmailTemplateRenderer.ValidateLayout(candidateLayout).Count == 0)
                    layout = candidateLayout;
                else
                    _logger.LogWarning("The layout of {TemplateKey} no longer validates; using the built-in layout.", templateKey);
            }
        }

        var rendered = EmailTemplateRenderer.Render(definition, content, values, layout);
        return rendered with { TemplateKey = templateKey, Locale = usedLocale, Version = version };
    }
}

/// <summary>Always the built-in wording. For hosts with no template store.</summary>
public sealed class DefaultEmailTemplateSource : IEmailTemplateSource
{
    public Task<StoredEmailTemplate?> FindActiveAsync(string templateKey, string? locale, CancellationToken ct = default) =>
        Task.FromResult<StoredEmailTemplate?>(null);
}

/// <summary>Counts nothing. For tests and hosts with no template store.</summary>
public sealed class NullEmailDeliveryRecorder : IEmailDeliveryRecorder
{
    public static readonly NullEmailDeliveryRecorder Instance = new();

    public Task RecordAsync(RenderedEmail email, bool succeeded, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Reads published templates from the notification service, which owns them.
///
/// Cached for <see cref="CacheDuration"/> per key and locale so a burst of invitations is one
/// round trip, which also bounds how long a publish takes to reach every sender. A failed read is
/// cached for a few seconds only and answers "use the default".
/// </summary>
public sealed class GrpcEmailTemplateSource : IEmailTemplateSource
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    private readonly NotificationGrpcService.NotificationGrpcServiceClient _client;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GrpcEmailTemplateSource> _logger;

    public GrpcEmailTemplateSource(
        NotificationGrpcService.NotificationGrpcServiceClient client,
        IMemoryCache cache,
        ILogger<GrpcEmailTemplateSource> logger)
    {
        _client = client;
        _cache = cache;
        _logger = logger;
    }

    public async Task<StoredEmailTemplate?> FindActiveAsync(string templateKey, string? locale, CancellationToken ct = default)
    {
        var cacheKey = $"email-template:{templateKey}:{locale ?? EmailLocales.Default}";
        if (_cache.TryGetValue(cacheKey, out StoredEmailTemplate? cached))
            return cached;

        try
        {
            var response = await _client.GetEmailTemplateAsync(
                new GetEmailTemplateRequest { TemplateKey = templateKey, Locale = locale ?? string.Empty },
                deadline: DateTime.UtcNow.Add(CallTimeout),
                cancellationToken: ct);

            var stored = response.Found ? FromResponse(response) : null;
            _cache.Set(cacheKey, stored, CacheDuration);
            return stored;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "Notification service did not answer for the {TemplateKey} email template; using the default.", templateKey);
            _cache.Set<StoredEmailTemplate?>(cacheKey, null, FailureCacheDuration);
            return null;
        }
    }

    public static StoredEmailTemplate FromResponse(GetEmailTemplateResponse response) =>
        new(response.Subject, response.Heading, response.BodyHtml, response.Version)
        {
            Preheader = response.Preheader,
            TextBody = string.IsNullOrWhiteSpace(response.TextBody) ? null : response.TextBody,
            LayoutHtml = string.IsNullOrWhiteSpace(response.LayoutHtml) ? null : response.LayoutHtml,
            LayoutText = string.IsNullOrWhiteSpace(response.LayoutText) ? null : response.LayoutText,
            LayoutDarkCss = string.IsNullOrWhiteSpace(response.LayoutDarkCss) ? null : response.LayoutDarkCss,
            Locale = EmailLocales.Normalize(response.Locale) ?? EmailLocales.Default,
        };
}

/// <summary>Reports each send to the notification service's delivery counters.</summary>
public sealed class GrpcEmailDeliveryRecorder : IEmailDeliveryRecorder
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    private readonly NotificationGrpcService.NotificationGrpcServiceClient _client;
    private readonly ILogger<GrpcEmailDeliveryRecorder> _logger;

    public GrpcEmailDeliveryRecorder(
        NotificationGrpcService.NotificationGrpcServiceClient client,
        ILogger<GrpcEmailDeliveryRecorder> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task RecordAsync(RenderedEmail email, bool succeeded, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(email.TemplateKey)) return;
        try
        {
            await _client.RecordEmailDeliveryAsync(
                new RecordEmailDeliveryRequest
                {
                    TemplateKey = email.TemplateKey,
                    Locale = email.Locale,
                    Succeeded = succeeded,
                    Version = email.Version,
                },
                deadline: DateTime.UtcNow.Add(CallTimeout),
                cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not record delivery of {TemplateKey}.", email.TemplateKey);
        }
    }
}

public static class EmailTemplateServiceCollectionExtensions
{
    /// <summary>
    /// Registers the composer and the delivery recorder over the notification service. The host
    /// must also register <see cref="NotificationGrpcService.NotificationGrpcServiceClient"/> — every
    /// sender already does, or does so beside this call — so the address stays in the host's
    /// Program.cs where the gRPC configuration coverage check can see it.
    /// </summary>
    public static IServiceCollection AddWarpTalkEmailTemplates(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.TryAddScoped<IEmailTemplateSource, GrpcEmailTemplateSource>();
        services.TryAddScoped<IEmailTemplateComposer, EmailTemplateComposer>();
        services.TryAddScoped<IEmailDeliveryRecorder, GrpcEmailDeliveryRecorder>();
        return services;
    }
}
