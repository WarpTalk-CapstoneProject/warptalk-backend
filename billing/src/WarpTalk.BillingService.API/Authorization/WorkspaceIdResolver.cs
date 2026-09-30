using System;
using Microsoft.AspNetCore.Mvc.Filters;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.API.Authorization;

/// <summary>
/// Finds the workspace a request is scoped to, for the workspace authorization filters. Shared so
/// the role gate and the internal-member gate cannot disagree about which workspace they checked.
/// </summary>
internal static class WorkspaceIdResolver
{
    private const string WorkspaceIdRouteKey = "workspaceId";

    public static bool TryGetWorkspaceId(ActionExecutingContext context, out Guid workspaceId)
    {
        if (TryParseWorkspaceId(context.RouteData.Values[WorkspaceIdRouteKey], out workspaceId))
        {
            return true;
        }

        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null)
            {
                continue;
            }

            if (argument is Guid id && id != Guid.Empty)
            {
                workspaceId = id;
                return true;
            }

            if (argument is IWorkspaceScopedRequest scopedRequest &&
                TryParseWorkspaceId(scopedRequest.WorkspaceId, out workspaceId))
            {
                return true;
            }
        }

        workspaceId = Guid.Empty;
        return false;
    }

    private static bool TryParseWorkspaceId(object? value, out Guid workspaceId)
    {
        workspaceId = Guid.Empty;

        return value switch
        {
            Guid id when id != Guid.Empty => (workspaceId = id) != Guid.Empty,
            string text when Guid.TryParse(text, out var id) && id != Guid.Empty => (workspaceId = id) != Guid.Empty,
            _ => false
        };
    }
}
