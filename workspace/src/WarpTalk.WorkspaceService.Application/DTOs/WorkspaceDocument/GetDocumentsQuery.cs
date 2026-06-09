using System;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

public record GetDocumentsQuery
{
    public int Page { get; init; }
    public int PageSize { get; init; }
    public string? Search { get; init; }

    public GetDocumentsQuery(int Page = 1, int PageSize = 10, string? Search = null)
    {
        this.Page = Page <= 0 ? 1 : Page;
        this.PageSize = PageSize <= 0 ? 10 : PageSize;
        this.Search = Search;
    }
}
