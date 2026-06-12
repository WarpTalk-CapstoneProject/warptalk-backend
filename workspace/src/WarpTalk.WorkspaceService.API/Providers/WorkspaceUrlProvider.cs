using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;

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
            return string.Format(WorkspaceDocumentConstants.DownloadUrlFormat, workspaceId, documentId);
        }

        return _linkGenerator.GetUriByAction(
            httpContext,
            action: "DownloadDocument",
            controller: "WorkspaceDocuments",
            values: new { workspaceId, documentId }
        ) ?? string.Format(WorkspaceDocumentConstants.DownloadUrlFormat, workspaceId, documentId);
    }
}
