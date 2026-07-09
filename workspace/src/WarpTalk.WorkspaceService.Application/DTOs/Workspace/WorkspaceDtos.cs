using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Application.DTOs.Workspace;

public record CreateWorkspaceRequest(
    string Name,
    string? LogoUrl,
    List<string>? VerifiedDomains = null,
    bool? RequireVerifiedDomainForInternal = null
);

public record GetWorkspacesQuery
{
    private readonly int _page = 1;
    private readonly int _pageSize = 10;

    public int Page 
    { 
        get => _page; 
        init => _page = value <= 0 ? 1 : value; 
    }
    
    public int PageSize 
    { 
        get => _pageSize; 
        init => _pageSize = value <= 0 ? 10 : value; 
    }
    
    public string? Search { get; init; }
}

public record WorkspaceDto(
    Guid Id,
    string Name,
    string Slug,
    string? LogoUrl,
    string Role,
    DateTime CreatedAt
);

public record PagedResult<T>(
    List<T> Items,
    int Page,
    int PageSize,
    int Total
);

public record SelectWorkspaceResponse(
    Guid SelectedWorkspaceId,
    string Name,
    string Slug
);
