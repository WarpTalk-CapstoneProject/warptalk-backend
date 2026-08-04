using WarpTalk.WorkspaceService.Domain.Enums;

namespace WarpTalk.WorkspaceService.Application.Helpers;

public static class JoinRequestSuggestedActions
{
    public const string EnableExternalCollaboration = "EnableExternalCollaboration";
    public const string AddVerifiedDomain = "AddVerifiedDomain";
    public const string RejectRequest = "RejectRequest";
}

public sealed record JoinRequestEligibility(
    MembershipType InferredMembershipType,
    IReadOnlyList<string> AllowedFinalMembershipTypes,
    bool RequiresPolicyAction,
    string? PolicyReason,
    IReadOnlyList<string> SuggestedActions);
