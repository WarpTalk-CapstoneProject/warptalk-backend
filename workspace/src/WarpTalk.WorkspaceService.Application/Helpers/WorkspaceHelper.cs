using System;
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

    public static async Task<bool> IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync(IUnitOfWork unitOfWork, Guid userId, string userEmail, CancellationToken ct)
    {
        var memberships = await unitOfWork.WorkspaceMemberRepository.FindAsync(
            m => m.UserId == userId && m.RemovedAt == null,
            "Workspace",
            ct);

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
        if (!config.RequireVerifiedDomainForInternal)
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
        var emailDomain = emailAddress.Domain;

        var isDomainVerified = await unitOfWork.Repository<WorkspaceVerifiedDomain>().AnyAsync(
            vd => vd.WorkspaceId == workspaceId 
                  && vd.Domain.ToLower() == emailDomain.ToLower() 
                  && vd.Status == "verified" 
                  && vd.VerifiedAt != null 
                  && vd.RevokedAt == null, 
            ct);
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
        if (!config.RequireVerifiedDomainForInternal)
        {
            return MembershipType.Internal;
        }

        if (string.IsNullOrEmpty(userEmail))
        {
            return MembershipType.External;
        }

        if (!EmailAddress.TryParse(userEmail, out var emailAddress) || emailAddress == null)
        {
            return MembershipType.External;
        }

        var emailDomain = emailAddress.Domain;
        
        var isDomainVerified = await unitOfWork.Repository<WorkspaceVerifiedDomain>().AnyAsync(
            vd => vd.WorkspaceId == workspace.Id 
                  && vd.Domain.ToLower() == emailDomain.ToLower() 
                  && vd.Status == "verified" 
                  && vd.VerifiedAt != null 
                  && vd.RevokedAt == null, 
            ct);
        
        return isDomainVerified ? MembershipType.Internal : MembershipType.External;
    }

    public static async Task<Guid?> GetWorkspaceIdVerifyingDomainAsync(IUnitOfWork unitOfWork, string domain, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;

        var verifiedDomain = await unitOfWork.Repository<WorkspaceVerifiedDomain>().FirstOrDefaultAsync(
            vd => vd.Domain.ToLower() == domain.ToLower() 
                  && vd.Status == "verified" 
                  && vd.VerifiedAt != null 
                  && vd.RevokedAt == null 
                  && vd.Workspace.IsActive 
                  && vd.Workspace.DeletedAt == null,
            "Workspace",
            ct);

        return verifiedDomain?.WorkspaceId;
    }
}
