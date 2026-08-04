using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;

namespace WarpTalk.WorkspaceService.Infrastructure.Adapters;

public class AiPolicyResolver : IAiPolicyResolver
{
    private readonly ILogger<AiPolicyResolver> _logger;

    public AiPolicyResolver(ILogger<AiPolicyResolver> logger)
    {
        _logger = logger;
    }

    public async Task<ResolvedPolicySettings> ResolvePolicySettingsAsync(
        IUnitOfWork unitOfWork,
        WorkspaceDocument document,
        CancellationToken ct = default)
    {
        bool piiEnabled = true;
        bool dlpEnabled = false;
        var keywordsBlacklist = new System.Collections.Generic.List<string>();
        // WarpTalk has no local embedding provider in production; AI-context documents
        // must be allowed to use the external embedding provider. The policy field is
        // retained for compatibility, but it no longer gates ingestion.
        const bool allowExternalLlm = true;

        // A. Parse Document-level AI Usage Policy if present
        AiUsagePolicyConfiguration? docPolicy = null;
        if (!string.IsNullOrWhiteSpace(document.AiUsagePolicy))
        {
            try
            {
                docPolicy = JsonSerializer.Deserialize<AiUsagePolicyConfiguration>(
                    document.AiUsagePolicy,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize AiUsagePolicy for document {DocumentId}", document.Id);
            }
        }

        // B. Parse Workspace-level default configurations
        WorkspaceConfiguration? wsConfig = null;
        var workspace = await unitOfWork.WorkspaceRepository.GetByIdAsync(document.WorkspaceId, ct);
        if (workspace != null)
        {
            wsConfig = WorkspaceHelper.GetWorkspaceConfig(workspace);
        }

        // C. Apply Hierarchy & Fallbacks
        if (docPolicy?.RedactPii != null)
        {
            piiEnabled = docPolicy.RedactPii.Enabled;
        }
        else if (wsConfig?.AiUsagePolicy?.RedactPii != null)
        {
            piiEnabled = wsConfig.AiUsagePolicy.RedactPii.Enabled;
        }

        if (docPolicy?.Dlp != null)
        {
            dlpEnabled = docPolicy.Dlp.Enabled;
            keywordsBlacklist = docPolicy.Dlp.KeywordsBlacklist;
        }
        else if (wsConfig?.AiUsagePolicy?.Dlp != null)
        {
            dlpEnabled = wsConfig.AiUsagePolicy.Dlp.Enabled;
            keywordsBlacklist = wsConfig.AiUsagePolicy.Dlp.KeywordsBlacklist;
        }

        return new ResolvedPolicySettings(piiEnabled, dlpEnabled, keywordsBlacklist, allowExternalLlm);
    }
}
