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
        var relativePath = _linkGenerator.GetPathByAction(
            action: "DownloadDocument",
            controller: "WorkspaceDocuments",
            values: new { workspaceId, documentId }
        ) ?? $"/api/v1/workspaces/{workspaceId}/documents/{documentId}/download";

        if (httpContext?.Request != null)
        {
            return $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{relativePath}";
        }

        return relativePath;
    }
}
