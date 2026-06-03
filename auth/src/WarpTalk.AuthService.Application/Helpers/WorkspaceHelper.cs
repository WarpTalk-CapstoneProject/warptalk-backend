using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Enums;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Domain.Settings;
using WarpTalk.AuthService.Domain.ValueObjects;

namespace WarpTalk.AuthService.Application.Helpers;

public static class WorkspaceHelper
{
    public static WorkspaceConfiguration GetWorkspaceConfig(Workspace workspace)
    {
        if (string.IsNullOrEmpty(workspace.Settings))
        {
            return new WorkspaceConfiguration();
        }
        try
        {
            return JsonSerializer.Deserialize<WorkspaceConfiguration>(workspace.Settings, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new WorkspaceConfiguration();
        }
        catch
        {
            return new WorkspaceConfiguration();
        }
    }

    public static async Task<bool> IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync(IUnitOfWork unitOfWork, Guid userId, CancellationToken ct)
    {
        var user = await unitOfWork.UserRepository.GetByIdAsync(userId, ct);
        if (user == null || string.IsNullOrEmpty(user.Email)) return false;

        if (!EmailAddress.TryParse(user.Email, out var emailAddress) || emailAddress == null) return false;
        var userDomain = emailAddress.Domain;

        var memberships = await unitOfWork.WorkspaceMemberRepository.FindAsync(
            m => m.UserId == userId && m.RemovedAt == null,
            "Workspace",
            ct);

        foreach (var membership in memberships)
        {
            var ws = membership.Workspace;
            if (ws != null)
            {
                var config = GetWorkspaceConfig(ws);
                if (config.VerifiedDomains != null && config.VerifiedDomains.Any(vd => string.Equals(vd.Trim(), userDomain, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static async Task<bool> IsUserExternalMemberAsync(IUnitOfWork unitOfWork, Guid workspaceId, Guid userId, CancellationToken ct)
    {
        var workspace = await unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
        if (workspace == null)
        {
            return false;
        }

        var user = await unitOfWork.UserRepository.GetByIdAsync(userId, ct);
        if (user == null || string.IsNullOrEmpty(user.Email))
        {
            return true;
        }

        if (!EmailAddress.TryParse(user.Email, out var emailAddress) || emailAddress == null)
        {
            return true;
        }
        var emailDomain = emailAddress.Domain;
        var config = GetWorkspaceConfig(workspace);

        var isDomainVerified = config.VerifiedDomains != null && config.VerifiedDomains.Any(vd => string.Equals(vd.Trim(), emailDomain, StringComparison.OrdinalIgnoreCase));
        return !isDomainVerified;
    }

    public static MembershipType DetermineMembershipType(WorkspaceMember member, Workspace? workspace)
    {
        if (workspace == null)
        {
            return MembershipType.Internal;
        }

        var user = member.User;
        if (user == null || string.IsNullOrEmpty(user.Email))
        {
            return MembershipType.External;
        }

        if (!EmailAddress.TryParse(user.Email, out var emailAddress) || emailAddress == null)
        {
            return MembershipType.External;
        }

        var emailDomain = emailAddress.Domain;
        var config = GetWorkspaceConfig(workspace);
        var isDomainVerified = config.VerifiedDomains != null && config.VerifiedDomains.Any(vd => string.Equals(vd.Trim(), emailDomain, StringComparison.OrdinalIgnoreCase));
        
        return isDomainVerified ? MembershipType.Internal : MembershipType.External;
    }

    public static async Task<Guid?> GetWorkspaceIdVerifyingDomainAsync(IUnitOfWork unitOfWork, string domain, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;

        var workspaces = await unitOfWork.WorkspaceRepository.FindAsync(
            w => w.IsActive && w.DeletedAt == null,
            "",
            ct);

        foreach (var ws in workspaces)
        {
            var config = GetWorkspaceConfig(ws);
            if (config.VerifiedDomains != null && config.VerifiedDomains.Any(vd => string.Equals(vd.Trim(), domain, StringComparison.OrdinalIgnoreCase)))
            {
                return ws.Id;
            }
        }

        return null;
    }
}
