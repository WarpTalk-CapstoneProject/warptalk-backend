using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Application.DTOs.Workspace;

public record CreateWorkspaceRequest(
    string Name,
    string? LogoUrl,
    List<string>? VerifiedDomains = null,
    bool? RequireVerifiedDomainForInternal = null
);

/// <summary>
/// Renaming only ever touches the display name. The slug stays exactly as it was created:
/// it is the primary lookup key for every [workspaceSlug] route and for the invitation join
/// flow, so regenerating it would break every member's stored links and free the old slug up
/// for squatting.
/// </summary>
public record RenameWorkspaceRequest(
    string Name
);

public record GetWorkspacesQuery
{
    public int Page { get; init; }
    public int PageSize { get; init; }
    public string? Search { get; init; }
    public string? Kind { get; init; }

    public GetWorkspacesQuery(int Page = 1, int PageSize = 10, string? Search = null, string? Kind = null)
    {
        this.Page = Page <= 0 ? 1 : Page;
        this.PageSize = PageSize <= 0 ? 10 : PageSize;
        this.Search = Search;
        this.Kind = Kind;
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
    string DefaultLanguage = "en",
    bool CanApproveDocuments = false
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
