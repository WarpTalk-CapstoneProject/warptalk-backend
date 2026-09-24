using Grpc.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WarpTalk.Shared.Protos;

namespace WarpTalk.Shared.Email;

/// <summary>Where a sender finds the admin-edited version of an email, if there is one.</summary>
public interface IEmailTemplateSource
{
    /// <summary>The active stored template, or null to use the built-in wording.</summary>
    Task<StoredEmailTemplate?> FindActiveAsync(string templateKey, CancellationToken ct = default);
}

/// <summary>
/// What every sender calls to build an email. Reads the stored template first and falls back to
/// the catalog default — so an admin's edit is what goes out, and an outage of the store never
/// stops a verification email from going out at all.
/// </summary>
public interface IEmailTemplateComposer
{
    Task<RenderedEmail> ComposeAsync(
        string templateKey,
        IReadOnlyDictionary<string, string> values,
        CancellationToken ct = default);
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
        CancellationToken ct = default)
    {
        var definition = EmailTemplateCatalog.Get(templateKey);
        var content = definition.Default;

        StoredEmailTemplate? stored = null;
        try
        {
            stored = await _source.FindActiveAsync(templateKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail open to the built-in wording: a customised email that cannot be read is still
            // better sent plain than not sent, and "verify your email" must never wait on this.
            _logger.LogWarning(ex, "Could not read the stored {TemplateKey} email template; sending the default.", templateKey);
        }

        if (stored is not null)
        {
            var candidate = new EmailTemplateContent(stored.Subject, stored.Heading, stored.BodyHtml);
            var issues = EmailTemplateRenderer.Validate(definition, candidate);
            if (issues.Count == 0)
            {
                content = candidate;
            }
            else
            {
                // Saved templates are validated on save, so this means the catalog moved under a
                // stored row (a variable was renamed or became required). Sending it would ship a
                // broken email; the default cannot be broken.
                _logger.LogWarning(
                    "Stored {TemplateKey} email template v{Version} no longer validates ({Issue}); sending the default.",
                    templateKey, stored.Version, issues[0].Message);
            }
        }

        return EmailTemplateRenderer.Render(definition, content, values);
    }
}

/// <summary>Always the built-in wording. For hosts with no template store.</summary>
public sealed class DefaultEmailTemplateSource : IEmailTemplateSource
{
    public Task<StoredEmailTemplate?> FindActiveAsync(string templateKey, CancellationToken ct = default) =>
        Task.FromResult<StoredEmailTemplate?>(null);
}

/// <summary>
/// Reads stored templates from the notification service, which owns them.
///
/// Cached for <see cref="CacheDuration"/> per key so a burst of invitations is one round trip,
/// which also bounds how long an admin's save takes to reach every sender. A failed read is cached
/// for a few seconds only and answers "use the default".
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

    public async Task<StoredEmailTemplate?> FindActiveAsync(string templateKey, CancellationToken ct = default)
    {
        var cacheKey = $"email-template:{templateKey}";
        if (_cache.TryGetValue(cacheKey, out StoredEmailTemplate? cached))
            return cached;

        try
        {
            var response = await _client.GetEmailTemplateAsync(
                new GetEmailTemplateRequest { TemplateKey = templateKey },
                deadline: DateTime.UtcNow.Add(CallTimeout),
                cancellationToken: ct);

            var stored = response.Found
                ? new StoredEmailTemplate(response.Subject, response.Heading, response.BodyHtml, response.Version)
                : null;
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
}

public static class EmailTemplateServiceCollectionExtensions
{
    /// <summary>
    /// Registers the composer over the notification service's template store. The host must also
    /// register <see cref="NotificationGrpcService.NotificationGrpcServiceClient"/> — every sender
    /// already does, or does so beside this call — so the address stays in the host's Program.cs
    /// where the gRPC configuration coverage check can see it.
    /// </summary>
    public static IServiceCollection AddWarpTalkEmailTemplates(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.TryAddScoped<IEmailTemplateSource, GrpcEmailTemplateSource>();
        services.TryAddScoped<IEmailTemplateComposer, EmailTemplateComposer>();
        return services;
    }
}
