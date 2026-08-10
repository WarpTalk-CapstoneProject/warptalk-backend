using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.ValueObjects;

namespace WarpTalk.WorkspaceService.Application.Helpers;

/// <summary>
/// The one place that decides whether a (workspace, email, membership type, role) combination
/// is permitted.
///
/// It exists because the create path and the accept path used to carry two separate copies of
/// these rules, and the copies disagreed. Creating an invitation matched the invitee's domain
/// through <see cref="WorkspaceHelper.ResolveMembershipType"/>, which honours AllowSubdomains;
/// accepting one re-matched it with an inline equality query, which does not. A workspace with
/// AllowSubdomains on could therefore issue an invitation to a subdomain address that nobody
/// could ever accept. The role rule diverged the same way: create refused an External invitee
/// holding anything but Member, accept never checked at all.
///
/// Both paths now call <see cref="ValidateAsync"/>, so a rule can only ever be added in one
/// place and cannot drift apart again.
/// </summary>
public static class WorkspaceInvitationPolicy
{
    /// <summary>
    /// What the current settings permit for one invitee address. This is advice for the invite
    /// form — which option to pre-select, which to disable and why — never the decision itself.
    /// <see cref="ValidateAsync"/> re-checks server-side on every write.
    /// </summary>
    public sealed record Evaluation(
        MembershipType SuggestedMembershipType,
        IReadOnlyList<string> AllowedMembershipTypes,
        bool RequireVerifiedDomainForInternal,
        bool AllowExternalCollaboration,
        bool AllowSubdomains,
        bool IsEmailDomainVerified,
        bool IsPublicEmailDomain,
        string? InternalDisabledReason,
        string? ExternalDisabledReason);

    public static async Task<Evaluation> EvaluateAsync(
        IUnitOfWork unitOfWork,
        Workspace workspace,
        string? email,
        CancellationToken ct = default)
    {
        var config = WorkspaceHelper.GetWorkspaceConfig(workspace);
        var requireVerifiedDomain = config.RequireVerifiedDomainForInternal;

        var domain = !string.IsNullOrWhiteSpace(email)
            && EmailAddress.TryParse(email!, out var parsed)
            && parsed != null
                ? parsed.Domain
                : string.Empty;

        var isPublicDomain = domain.Length > 0 && EmailAddress.IsPublicDomainName(domain);
        var isDomainVerified = domain.Length > 0
            && await WorkspaceHelper.IsEmailDomainVerifiedAsync(unitOfWork, workspace, domain, ct);

        // With the policy off the workspace draws no internal/external line from domains at all,
        // so Internal is always on the table (BR-140-005). With it on, only a verified domain
        // earns Internal.
        var internalAllowed = !requireVerifiedDomain || isDomainVerified;
        var externalAllowed = config.AllowExternalCollaboration;

        var allowed = new List<string>();
        if (internalAllowed) allowed.Add(MembershipType.Internal.ToString());
        if (externalAllowed) allowed.Add(MembershipType.External.ToString());

        var suggested = internalAllowed ? MembershipType.Internal : MembershipType.External;

        string? internalReason = null;
        if (!internalAllowed)
        {
            internalReason = isPublicDomain
                ? WorkspaceConstants.Errors.CannotInviteInternalWithPublicDomain
                : WorkspaceConstants.Errors.CannotInviteInternalWithoutVerifiedDomain;
        }

        return new Evaluation(
            suggested,
            allowed,
            requireVerifiedDomain,
            config.AllowExternalCollaboration,
            workspace.AllowSubdomains,
            isDomainVerified,
            isPublicDomain,
            internalReason,
            externalAllowed ? null : WorkspaceConstants.Errors.ExternalCollaborationNotAllowed);
    }

    /// <summary>
    /// Decides a concrete membership type + role against the settings in force right now.
    /// </summary>
    /// <param name="roleName">
    /// Pass null only where no role has been chosen yet. Supplying it is what stops an External
    /// member from carrying an Admin role, so both the create and the accept path pass one.
    /// </param>
    public static async Task<Result> ValidateAsync(
        IUnitOfWork unitOfWork,
        Workspace workspace,
        string email,
        MembershipType membershipType,
        string? roleName,
        CancellationToken ct = default)
    {
        if (!EmailAddress.TryParse(email, out var emailAddress) || emailAddress == null)
        {
            return Result.Failure(WorkspaceConstants.Errors.InvalidEmailFormat, ErrorCodes.ValidationError);
        }

        // GetWorkspaceConfig mirrors the dedicated columns over whatever the settings JSON says,
        // so reading the flags off the config here reads the columns. Nothing in this method may
        // consult config.VerifiedDomains — a stale JSON list is not evidence of live policy, and
        // treating it as such is what made three invitations on `testworkspace` unacceptable
        // (WT-179).
        var config = WorkspaceHelper.GetWorkspaceConfig(workspace);

        if (membershipType == MembershipType.Internal)
        {
            if (!config.RequireVerifiedDomainForInternal)
            {
                // No domain rule is in force, so there is nothing to validate — not even the
                // public-domain rule, which is a special case of the verified-domain rule rather
                // than a standalone one.
                return Result.Success();
            }

            if (EmailAddress.IsPublicDomainName(emailAddress.Domain))
            {
                return Result.Failure(WorkspaceConstants.Errors.CannotInviteInternalWithPublicDomain, ErrorCodes.ValidationError);
            }

            var isDomainVerified = await WorkspaceHelper.IsEmailDomainVerifiedAsync(
                unitOfWork, workspace, emailAddress.Domain, ct);
            return isDomainVerified
                ? Result.Success()
                : Result.Failure(WorkspaceConstants.Errors.CannotInviteInternalWithoutVerifiedDomain, ErrorCodes.ValidationError);
        }

        if (!config.AllowExternalCollaboration)
        {
            return Result.Failure(WorkspaceConstants.Errors.ExternalCollaborationNotAllowed, ErrorCodes.Forbidden);
        }

        if (roleName != null && !roleName.IsMember())
        {
            return Result.Failure(WorkspaceConstants.Errors.ExternalMemberMustHaveMemberRole, ErrorCodes.ValidationError);
        }

        return Result.Success();
    }
}
