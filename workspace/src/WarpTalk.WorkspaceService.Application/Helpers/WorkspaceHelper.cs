using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;
using WarpTalk.WorkspaceService.Domain.ValueObjects;

namespace WarpTalk.WorkspaceService.Application.Helpers;

public static class WorkspaceHelper
{
    public static WorkspaceConfiguration GetWorkspaceConfig(Workspace workspace)
    {
        WorkspaceConfiguration config;
        if (string.IsNullOrEmpty(workspace.Settings))
        {
            config = new WorkspaceConfiguration();
        }
        else
        {
            try
            {
                config = JsonSerializer.Deserialize<WorkspaceConfiguration>(
                    workspace.Settings,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new WorkspaceConfiguration();
            }
            catch
            {
                config = new WorkspaceConfiguration();
            }
        }

        // Dedicated columns are the authorization source of truth. Mirroring
        // them into the JSON DTO prevents stale settings JSON from changing policy.
        config.AllowExternalCollaboration = workspace.AllowExternalCollaboration;
        config.RequireVerifiedDomainForInternal = workspace.RequireVerifiedDomainForInternal;
        return config;
    }

    public static async Task<bool> IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync(IUnitOfWork unitOfWork, Guid userId, string userEmail, CancellationToken ct)
    {
        var memberships = await unitOfWork.WorkspaceMemberRepository.FindAsync(
            m => m.UserId == userId && m.RemovedAt == null,
            "Workspace",
            ct);

        // "Enterprise" is the RequireVerifiedDomainForInternal column and nothing else. A
        // VerifiedDomains list left behind in the settings JSON used to count too, which made
        // workspaces that had switched the policy off still behave as if it were on — the
        // WT-179 incident. Stale JSON is not evidence of live policy.
        return memberships.Any(m =>
            string.Equals(m.MembershipType, MembershipType.Internal.ToString(), StringComparison.OrdinalIgnoreCase)
            && m.Workspace != null
            && GetWorkspaceConfig(m.Workspace).RequireVerifiedDomainForInternal);
    }

    public static async Task<bool> IsUserExternalMemberAsync(IUnitOfWork unitOfWork, Guid workspaceId, string userEmail, CancellationToken ct)
    {
        var workspace = await unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
        if (workspace == null)
        {
            return false;
        }

        var config = GetWorkspaceConfig(workspace);
        if (!workspace.RequireVerifiedDomainForInternal && !config.RequireVerifiedDomainForInternal)
        {
            return false;
        }

        if (string.IsNullOrEmpty(userEmail))
        {
            return true;
        }

        if (!EmailAddress.TryParse(userEmail, out var emailAddress) || emailAddress == null)
        {
            return true;
        }
        var isDomainVerified = await IsEmailDomainVerifiedAsync(unitOfWork, workspace, emailAddress.Domain, ct);
        return !isDomainVerified;
    }

    public static async Task<bool> IsUserExternalMemberAsync(IUnitOfWork unitOfWork, Guid workspaceId, Guid userId, CancellationToken ct)
    {
        var member = await unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
        if (member == null) return true;
        return string.Equals(member.MembershipType, MembershipType.External.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<MembershipType> DetermineMembershipTypeAsync(IUnitOfWork unitOfWork, string? userEmail, Workspace? workspace, CancellationToken ct)
    {
        if (workspace == null)
        {
            return MembershipType.Internal;
        }

        var config = GetWorkspaceConfig(workspace);
        if (!workspace.RequireVerifiedDomainForInternal && !config.RequireVerifiedDomainForInternal)
        {
            return MembershipType.Internal;
        }

        var verifiedDomains = await GetActiveVerifiedDomainsAsync(unitOfWork, workspace.Id, ct);

        return ResolveMembershipType(
            userEmail,
            verifiedDomains,
            requireVerifiedDomain: true,
            workspace.AllowSubdomains);
    }

    public static async Task<MembershipType> DetermineJoinRequestMembershipTypeAsync(
        IUnitOfWork unitOfWork,
        string? userEmail,
        Workspace workspace,
        CancellationToken ct)
    {
        var verifiedDomains = await GetActiveVerifiedDomainsAsync(unitOfWork, workspace.Id, ct);

        // Join Requests are deliberately conservative: without proof of a
        // verified workspace domain, the request remains provisional External.
        return ResolveMembershipType(
            userEmail,
            verifiedDomains,
            requireVerifiedDomain: true,
            workspace.AllowSubdomains);
    }

    public static async Task<JoinRequestEligibility> EvaluateJoinRequestEligibilityAsync(
        IUnitOfWork unitOfWork,
        string? userEmail,
        Guid? userId,
        Workspace workspace,
        CancellationToken ct)
    {
        var config = GetWorkspaceConfig(workspace);
        var verifiedDomains = await GetActiveVerifiedDomainsAsync(unitOfWork, workspace.Id, ct);

        var inferredMembershipType = ResolveMembershipType(
            userEmail,
            verifiedDomains,
            requireVerifiedDomain: true,
            workspace.AllowSubdomains);

        if (inferredMembershipType == MembershipType.Internal)
        {
            var isEnterpriseWorkspace = workspace.RequireVerifiedDomainForInternal
                || config.RequireVerifiedDomainForInternal
                || verifiedDomains.Any();

            if (userId.HasValue
                && isEnterpriseWorkspace
                && await IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync(unitOfWork, userId.Value, userEmail ?? string.Empty, ct))
            {
                return new JoinRequestEligibility(
                    inferredMembershipType,
                    Array.Empty<string>(),
                    RequiresPolicyAction: false,
                    PolicyReason: "Requester is already an internal member of another Enterprise Workspace.",
                    SuggestedActions: new[] { JoinRequestSuggestedActions.RejectRequest });
            }

            return new JoinRequestEligibility(
                inferredMembershipType,
                new[] { MembershipType.Internal.ToString() },
                RequiresPolicyAction: false,
                PolicyReason: "Requester email matches a verified workspace domain.",
                SuggestedActions: Array.Empty<string>());
        }

        if (config.AllowExternalCollaboration)
        {
            return new JoinRequestEligibility(
                inferredMembershipType,
                new[] { MembershipType.External.ToString() },
                RequiresPolicyAction: false,
                PolicyReason: "Requester email does not match a verified workspace domain, but external collaboration is enabled.",
                SuggestedActions: Array.Empty<string>());
        }

        return new JoinRequestEligibility(
            inferredMembershipType,
            Array.Empty<string>(),
            RequiresPolicyAction: true,
            PolicyReason: "Requester email does not match a verified workspace domain and external collaboration is disabled.",
            SuggestedActions: new[]
            {
                JoinRequestSuggestedActions.EnableExternalCollaboration,
                JoinRequestSuggestedActions.AddVerifiedDomain,
                JoinRequestSuggestedActions.RejectRequest
            });
    }

    public static MembershipType ResolveMembershipType(
        string? userEmail,
        IEnumerable<string> verifiedDomains,
        bool requireVerifiedDomain,
        bool allowSubdomains)
    {
        if (!requireVerifiedDomain)
        {
            return MembershipType.Internal;
        }

        if (string.IsNullOrWhiteSpace(userEmail)
            || !EmailAddress.TryParse(userEmail, out var emailAddress)
            || emailAddress == null)
        {
            return MembershipType.External;
        }

        var emailDomain = emailAddress.Domain.ToLowerInvariant();
        var matches = verifiedDomains.Any(domain =>
        {
            var verifiedDomain = domain.Trim().TrimStart('@').ToLowerInvariant();
            return emailDomain == verifiedDomain
                || (allowSubdomains && emailDomain.EndsWith($".{verifiedDomain}", StringComparison.Ordinal));
        });

        return matches ? MembershipType.Internal : MembershipType.External;
    }

    /// <summary>
    /// The workspace's live verified domains, normalised and de-duplicated.
    ///
    /// <c>workspace_verified_domains</c> is the only record of them.
    /// <see cref="WorkspaceConfiguration.VerifiedDomains"/> is a display mirror and nothing may
    /// make a policy decision from it: the two were read interchangeably once, and a settings JSON
    /// list that no longer matched the table went on deciding who counted as Internal (WT-179).
    ///
    /// Four call sites used to carry their own copy of this query. They agreed, which is why the
    /// duplication was survivable — but a fifth copy that disagreed is exactly the shape of that
    /// incident, so there is now one.
    /// </summary>
    public static async Task<List<string>> GetActiveVerifiedDomainsAsync(
        IUnitOfWork unitOfWork,
        Guid workspaceId,
        CancellationToken ct)
    {
        var verifiedStatus = VerifiedDomainStatus.Verified.ToString().ToLowerInvariant();
        var verifiedDomains = await unitOfWork.WorkspaceVerifiedDomainRepository.FindAsync(
            vd => vd.WorkspaceId == workspaceId
                  && vd.Status == verifiedStatus
                  && vd.VerifiedAt != null
                  && vd.RevokedAt == null,
            "",
            ct);

        return verifiedDomains
            .Select(vd => vd.Domain.Trim().TrimStart('@').ToLowerInvariant())
            .Where(domain => domain.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<bool> IsEmailDomainVerifiedAsync(
        IUnitOfWork unitOfWork,
        Workspace workspace,
        string emailDomain,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(emailDomain))
        {
            return false;
        }

        var normalizedDomain = emailDomain.Trim().TrimStart('@').ToLowerInvariant();
        var verifiedDomains = await GetActiveVerifiedDomainsAsync(unitOfWork, workspace.Id, ct);

        return verifiedDomains.Any(verifiedDomain =>
            normalizedDomain == verifiedDomain
            || (workspace.AllowSubdomains && normalizedDomain.EndsWith($".{verifiedDomain}", StringComparison.Ordinal)));
    }

    public static async Task<Guid?> GetWorkspaceIdVerifyingDomainAsync(IUnitOfWork unitOfWork, string domain, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;

        var verifiedStatus = VerifiedDomainStatus.Verified.ToString().ToLower();
        var verifiedDomain = await unitOfWork.WorkspaceVerifiedDomainRepository.FirstOrDefaultAsync(
            vd => vd.Domain.ToLower() == domain.ToLower()
                  && vd.Status == verifiedStatus
                  && vd.VerifiedAt != null
                  && vd.RevokedAt == null
                  && vd.Workspace.IsActive
                  && vd.Workspace.DeletedAt == null,
            "Workspace",
            ct);

        return verifiedDomain?.WorkspaceId;
    }

    public static bool AreEquivalentAiPolicies(AiUsagePolicyConfiguration? current, AiUsagePolicyConfiguration? requested)
    {
        var left = current ?? new AiUsagePolicyConfiguration(true, null, null, null, true);
        var right = requested ?? new AiUsagePolicyConfiguration(true, null, null, null, true);
        var leftDlp = left.Dlp;
        var rightDlp = right.Dlp;
        return left.AllowExternalLlm == right.AllowExternalLlm
            && left.RedactPii?.Enabled == right.RedactPii?.Enabled
            && leftDlp?.Enabled == rightDlp?.Enabled
            && (leftDlp?.KeywordsBlacklist ?? new List<string>()).SequenceEqual(rightDlp?.KeywordsBlacklist ?? new List<string>(), StringComparer.OrdinalIgnoreCase)
            && left.TranslationProfile?.TranslationTone == right.TranslationProfile?.TranslationTone
            && left.TranslationProfile?.LanguageSpecificRules?.VietnameseHonorificStyle == right.TranslationProfile?.LanguageSpecificRules?.VietnameseHonorificStyle
            && left.TranslationProfile?.LanguageSpecificRules?.JapaneseHonorificStyle == right.TranslationProfile?.LanguageSpecificRules?.JapaneseHonorificStyle
            && (left.UseGlobalGlossary ?? true) == (right.UseGlobalGlossary ?? true);
    }
}
