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
        //
        // WT-417 adds `DeletedAt == null`, and it is the half that bit hardest. This rule is
        // "you may be internal to only one Enterprise workspace", so it reads every membership
        // the user holds — and a DELETED workspace still had rows here, because deleting one
        // stamped the workspace and left its memberships with RemovedAt NULL. The result was an
        // account permanently barred from joining any Enterprise workspace as Internal, by a
        // workspace that no longer exists, with a 403 naming a workspace the user cannot see, in
        // a listing it never appears in. Nothing in the product could have shown them why.
        //
        // SoftDeleteWorkspaceAsync now stamps those rows so the orphan is not manufactured in the
        // first place, and the backfill migration clears the ones already out there. This stays
        // regardless: a membership of a workspace that does not exist must never gate anything,
        // whatever left it lying around.
        return memberships.Any(m =>
            string.Equals(m.MembershipType, MembershipType.Internal.ToString(), StringComparison.OrdinalIgnoreCase)
            && m.Workspace != null
            && m.Workspace.DeletedAt == null);
    }


    public static async Task<bool> IsUserExternalMemberAsync(IUnitOfWork unitOfWork, Guid workspaceId, Guid userId, CancellationToken ct)
    {
        var member = await unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
        if (member == null) return true;
        return string.Equals(member.MembershipType, MembershipType.External.ToString(), StringComparison.OrdinalIgnoreCase);
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
        var requireVerifiedDomain = config.RequireVerifiedDomainForInternal;

        // A workspace with no domain policy draws the internal/external line by hand, so a join
        // request there is not decided by the requester's address — both classes are open and the
        // reviewing Admin picks one.
        //
        // This used to hard-code requireVerifiedDomain: true regardless of the workspace, which
        // meant such a workspace has no verified domains, so every requester inferred External,
        // so AllowedFinalMembershipTypes only ever offered External — and ApproveJoinRequestAsync
        // refused an Admin who chose Internal. There was no way at all to admit an internal member
        // through this path.
        if (!requireVerifiedDomain)
        {
            var allowedWithoutDomainPolicy = config.AllowExternalCollaboration
                ? new[] { MembershipType.Internal.ToString(), MembershipType.External.ToString() }
                : new[] { MembershipType.Internal.ToString() };

            return new JoinRequestEligibility(
                MembershipType.Internal,
                allowedWithoutDomainPolicy,
                RequiresPolicyAction: false,
                PolicyReason: "This workspace assigns membership manually, so the reviewer chooses the access type.",
                SuggestedActions: Array.Empty<string>());
        }

        var inferredMembershipType = ResolveMembershipType(
            userEmail,
            verifiedDomains,
            requireVerifiedDomain: true,
            workspace.AllowSubdomains);

        if (inferredMembershipType == MembershipType.Internal)
        {
            if (userId.HasValue
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

    /// <summary>
    /// Which workspace currently holds <paramref name="domain"/>, if any.
    ///
    /// Deliberately blind to the owning workspace's lifecycle. It used to skip suspended and
    /// soft-deleted workspaces, which disagreed with the partial unique index behind the same
    /// rule — the index only looks at <c>status</c>. A caller was told the domain was free,
    /// the INSERT then hit the index, and the request failed as a 500 instead of a refusal.
    ///
    /// Suspension is reversible, so it must not release a claim: the workspace is coming back
    /// and expects to still hold its domain. Deletion is terminal, and releases the claim by
    /// revoking the rows outright (see SoftDeleteWorkspaceAsync) rather than by being filtered
    /// out here — which keeps a single rule, "a domain is taken while its row is verified",
    /// true at both layers.
    /// </summary>
    public static async Task<Guid?> GetWorkspaceIdVerifyingDomainAsync(IUnitOfWork unitOfWork, string domain, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;

        var verifiedStatus = VerifiedDomainStatus.Verified.ToString().ToLower();
        var verifiedDomain = await unitOfWork.WorkspaceVerifiedDomainRepository.FirstOrDefaultAsync(
            vd => vd.Domain.ToLower() == domain.ToLower()
                  && vd.Status == verifiedStatus
                  && vd.VerifiedAt != null
                  && vd.RevokedAt == null,
            "Workspace",
            ct);

        return verifiedDomain?.WorkspaceId;
    }

    /// <summary>
    /// Restores the invariant that defines a workspace's membership policy:
    ///
    /// <code>require_verified_domain_for_internal == (active verified domains &gt; 0)</code>
    ///
    /// The column is derived, not configured. Holding a verified domain IS domain-verified
    /// membership; holding none IS manually-assigned membership. Nobody sets the flag — an
    /// Owner adds or revokes a domain and the policy follows, which is why the settings
    /// endpoint refuses the field outright.
    ///
    /// Every path that changes the domain list calls this, and no path sets the column
    /// directly. Three copies of one invariant is how WT-179 happened the first time.
    ///
    /// Returns the policy now in force.
    /// </summary>
    public static async Task<bool> RecomputeDomainPolicyAsync(
        IUnitOfWork unitOfWork,
        Workspace workspace,
        CancellationToken ct)
    {
        var activeDomains = await GetActiveVerifiedDomainsAsync(unitOfWork, workspace.Id, ct);
        var requireVerifiedDomain = activeDomains.Count > 0;

        if (workspace.RequireVerifiedDomainForInternal != requireVerifiedDomain)
        {
            workspace.RequireVerifiedDomainForInternal = requireVerifiedDomain;
            workspace.UpdatedAt = DateTime.UtcNow;
            unitOfWork.WorkspaceRepository.Update(workspace);
        }

        return requireVerifiedDomain;
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
