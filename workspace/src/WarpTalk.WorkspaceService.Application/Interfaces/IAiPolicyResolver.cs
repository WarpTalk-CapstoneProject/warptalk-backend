using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public record ResolvedPolicySettings(
    bool PiiEnabled,
    bool DlpEnabled,
    List<string>? KeywordsBlacklist,
    bool AllowExternalLlm);

public interface IAiPolicyResolver
{
    Task<ResolvedPolicySettings> ResolvePolicySettingsAsync(
        IUnitOfWork unitOfWork,
        WorkspaceDocument document,
        CancellationToken ct = default);
}
