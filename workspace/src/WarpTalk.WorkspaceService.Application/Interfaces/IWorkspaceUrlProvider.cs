using System;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IWorkspaceUrlProvider
{
    string GetDocumentDownloadUrl(Guid workspaceId, Guid documentId);
}
