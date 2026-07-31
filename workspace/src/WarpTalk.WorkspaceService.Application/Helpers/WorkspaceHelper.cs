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

        return memberships.Any(m => 
            string.Equals(m.MembershipType, MembershipType.Internal.ToString(), StringComparison.OrdinalIgnoreCase)
            && m.Workspace != null 
            && (m.Workspace.RequireVerifiedDomainForInternal 
                || GetWorkspaceConfig(m.Workspace).RequireVerifiedDomainForInternal 
                || GetWorkspaceConfig(m.Workspace).VerifiedDomains.Any()));
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

        var verifiedStatus = VerifiedDomainStatus.Verified.ToString().ToLowerInvariant();
        var verifiedDomains = await unitOfWork.WorkspaceVerifiedDomainRepository.FindAsync(
            vd => vd.WorkspaceId == workspace.Id
                  && vd.Status == verifiedStatus
                  && vd.VerifiedAt != null
                  && vd.RevokedAt == null,
            "",
            ct);

        return ResolveMembershipType(
            userEmail,
            verifiedDomains.Select(vd => vd.Domain),
            requireVerifiedDomain: true,
            workspace.AllowSubdomains);
    }

    public static async Task<MembershipType> DetermineJoinRequestMembershipTypeAsync(
        IUnitOfWork unitOfWork,
        string? userEmail,
        Workspace workspace,
        CancellationToken ct)
    {
        var verifiedStatus = VerifiedDomainStatus.Verified.ToString().ToLowerInvariant();
        var verifiedDomains = await unitOfWork.WorkspaceVerifiedDomainRepository.FindAsync(
            vd => vd.WorkspaceId == workspace.Id
                  && vd.Status == verifiedStatus
                  && vd.VerifiedAt != null
                  && vd.RevokedAt == null,
            "",
            ct);

        // Join Requests are deliberately conservative: without proof of a
        // verified workspace domain, the request remains provisional External.
        return ResolveMembershipType(
            userEmail,
            verifiedDomains.Select(vd => vd.Domain),
            requireVerifiedDomain: true,
            workspace.AllowSubdomains);
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
        var verifiedStatus = VerifiedDomainStatus.Verified.ToString().ToLowerInvariant();
        var verifiedDomains = await unitOfWork.WorkspaceVerifiedDomainRepository.FindAsync(
            vd => vd.WorkspaceId == workspace.Id
                  && vd.Status == verifiedStatus
                  && vd.VerifiedAt != null
                  && vd.RevokedAt == null,
            "",
            ct);

        return verifiedDomains.Any(vd =>
        {
            var verifiedDomain = vd.Domain.Trim().TrimStart('@').ToLowerInvariant();
            return normalizedDomain == verifiedDomain
                || (workspace.AllowSubdomains && normalizedDomain.EndsWith($".{verifiedDomain}", StringComparison.Ordinal));
        });
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

    public static async Task<MembershipType> DetermineJoinRequestMembershipTypeAsync(IUnitOfWork unitOfWork, string? userEmail, Workspace? workspace, CancellationToken ct)
    {
        return await DetermineMembershipTypeAsync(unitOfWork, userEmail, workspace, ct);
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
}
