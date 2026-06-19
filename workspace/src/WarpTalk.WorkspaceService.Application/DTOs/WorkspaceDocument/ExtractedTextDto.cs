using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

public record ExtractedTextDto(
    string FullText,
    List<ExtractedPageDto> Pages,
    List<ExtractedSheetDto> Sheets
);

public record ExtractedPageDto(int PageNumber, string Text);

public record ExtractedSheetDto(string SheetName, List<List<string>> Rows);
