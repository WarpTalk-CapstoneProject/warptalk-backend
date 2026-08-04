using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Interfaces;

namespace WarpTalk.Shared.Services;

public class EmailTemplateProvider : IEmailTemplateProvider
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<EmailTemplateProvider> _logger;

    public EmailTemplateProvider(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IMemoryCache memoryCache,
        ILogger<EmailTemplateProvider> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _memoryCache = memoryCache;
        _logger = logger;
    }

    public async Task<string> GetTemplateAsync(string templateName, CancellationToken ct = default)
    {
        var cacheKey = $"EmailTemplate_{templateName}";

        if (_memoryCache.TryGetValue(cacheKey, out string? cachedHtml) && !string.IsNullOrWhiteSpace(cachedHtml))
        {
            return cachedHtml;
        }

        string? html = null;

        // 1. Try fetching from Cloudflare R2 / S3 CDN if Resend:TemplateCdnUrl is configured
        var cdnBaseUrl = _configuration["Resend:TemplateCdnUrl"];
        if (!string.IsNullOrWhiteSpace(cdnBaseUrl))
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                var cdnUrl = $"{cdnBaseUrl.TrimEnd('/')}/{templateName}.html";
                _logger.LogInformation("Fetching email template {TemplateName} from Cloudflare CDN: {CdnUrl}", templateName, cdnUrl);
                html = await client.GetStringAsync(cdnUrl, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not fetch email template {TemplateName} from CDN. Falling back to local file template.", templateName);
            }
        }

        // 2. Try loading local template file packaged with Shared library / service binaries
        if (string.IsNullOrWhiteSpace(html))
        {
            var localPath = Path.Combine(AppContext.BaseDirectory, "Templates", $"{templateName}.html");
            if (File.Exists(localPath))
            {
                html = await File.ReadAllTextAsync(localPath, ct);
            }
        }

        // 3. Try loading local monorepo web template path (Dev fallback)
        if (string.IsNullOrWhiteSpace(html))
        {
            var monorepoWebPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "warptalk-web", "public", "templates", $"{templateName}.html");
            if (File.Exists(monorepoWebPath))
            {
                html = await File.ReadAllTextAsync(monorepoWebPath, ct);
            }
        }

        if (string.IsNullOrWhiteSpace(html))
        {
            throw new FileNotFoundException($"Email template '{templateName}.html' was not found on CDN, in local Templates directory, or monorepo path.");
        }

        _memoryCache.Set(cacheKey, html, TimeSpan.FromMinutes(15));
        return html;
    }
}
