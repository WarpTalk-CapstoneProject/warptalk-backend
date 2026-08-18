using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Application.DTOs.Workspace;

public record InitialWorkspaceInvitationDto(
    string Email,
    string RoleName,
    string MembershipType = "Internal"
);

public record CreateWorkspaceRequest(
    string Name,
    string? LogoUrl,
    List<string>? VerifiedDomains = null,
    bool? RequireVerifiedDomainForInternal = null,
    List<InitialWorkspaceInvitationDto>? InitialInvitations = null
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

/// <param name="CanCreateMeetings">
/// The selected member's own <c>can_create_meetings</c> flag, so the shell can hide an action the
/// server would refuse. WT-371 #2: an External member kept the full Internal UI — every New-meeting
/// button in the app — and only found out on submit, as a 403 from the meeting-creation policy.
///
/// Advisory, never the gate. The decision still belongs to
/// <c>WorkspaceDirectoryService.ValidateMeetingCreationAsync</c>, which reads the same column plus
/// tenant lifecycle and plan quota; this field only spares the user a dead-end. It defaults to true
/// so an older client, or a response deserialised without it, behaves exactly as before rather than
/// hiding meeting creation from everyone.
/// </param>
public record SelectWorkspaceResponse(
    Guid SelectedWorkspaceId,
    string Name,
    string Slug,
    string Role,
    string MembershipType,
    string DefaultLanguage = "en",
    bool CanCreateMeetings = true
);
