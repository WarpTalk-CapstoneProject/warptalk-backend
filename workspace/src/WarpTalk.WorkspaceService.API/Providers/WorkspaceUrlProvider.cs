using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.API.Providers;

public class WorkspaceUrlProvider : IWorkspaceUrlProvider
{
    private readonly LinkGenerator _linkGenerator;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public WorkspaceUrlProvider(
        LinkGenerator linkGenerator,
        IHttpContextAccessor httpContextAccessor)
    {
        _linkGenerator = linkGenerator;
        _httpContextAccessor = httpContextAccessor;
    }

    public string GetDocumentDownloadUrl(Guid workspaceId, Guid documentId)
    {
        var httpContext = _httpContextAccessor.HttpContext;

        if (httpContext == null)
        {
            // Fallback to relative URL if HTTP context is not available (e.g. background threads / unit tests)
            return $"/api/v1/workspaces/{workspaceId}/documents/{documentId}/download";
        }

        return _linkGenerator.GetUriByAction(
            httpContext,
            action: "DownloadDocument",
            controller: "WorkspaceDocuments",
            values: new { workspaceId, documentId }
        ) ?? $"/api/v1/workspaces/{workspaceId}/documents/{documentId}/download";
    }
}
