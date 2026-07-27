using System;
using System.IO;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

public record DocumentDownloadStreamDto(
    Stream Stream,
    string ContentType,
    string FileName
);
