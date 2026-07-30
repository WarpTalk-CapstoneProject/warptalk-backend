using System;
using WarpTalk.WorkspaceService.Application.DTOs.VerifiedDomain;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;

namespace WarpTalk.WorkspaceService.Application.Mappers.VerifiedDomain;

public static class VerifiedDomainMapper
{
    public static VerifiedDomainDto ToDto(this WorkspaceVerifiedDomain entity)
    {
        return new VerifiedDomainDto(
            Id: entity.Id,
            Domain: entity.Domain,
            Status: entity.Status,
            VerifiedAt: entity.VerifiedAt!.Value,
            RevokedAt: entity.RevokedAt,
            CreatedAt: entity.CreatedAt
        );
    }

    public static WorkspaceVerifiedDomain ToEntity(Guid workspaceId, string domain, Guid userId)
    {
        var now = DateTime.UtcNow;
        return new WorkspaceVerifiedDomain
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Domain = domain,
            Status = VerifiedDomainStatus.Verified.ToString().ToLower(),
            VerificationMethod = "trusted",
            VerificationToken = "N/A",
            VerifiedAt = now,
            VerifiedBy = userId,
            CreatedAt = now,
            CreatedBy = userId,
            UpdatedAt = now,
            UpdatedBy = userId
        };
    }
}
