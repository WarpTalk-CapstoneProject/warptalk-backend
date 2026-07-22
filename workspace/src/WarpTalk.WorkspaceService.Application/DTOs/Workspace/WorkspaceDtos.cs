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
    public int Page { get; init; }
    public int PageSize { get; init; }
    public string? Search { get; init; }

    public GetWorkspacesQuery(int Page = 1, int PageSize = 10, string? Search = null)
    {
        this.Page = Page <= 0 ? 1 : Page;
        this.PageSize = PageSize <= 0 ? 10 : PageSize;
        this.Search = Search;
    }
}

public record WorkspaceDto(
    Guid Id,
    string Name,
    string Slug,
    string? LogoUrl,
    string Role,
    DateTime CreatedAt,
    string MembershipType = "Internal",
    string DefaultLanguage = "en"
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
    string Slug,
    string DefaultLanguage = "en"
);
