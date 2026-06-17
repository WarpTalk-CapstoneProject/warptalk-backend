using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Application.Models;

public class ExtractedDocumentContent
{
    public string FullText { get; set; } = string.Empty;
    public List<ExtractedPage> Pages { get; set; } = new();
    public List<ExtractedSheet> Sheets { get; set; } = new();
}

public class ExtractedPage
{
    public int PageNumber { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class ExtractedSheet
{
    public string SheetName { get; set; } = string.Empty;
    public List<List<string>> Rows { get; set; } = new();
}
