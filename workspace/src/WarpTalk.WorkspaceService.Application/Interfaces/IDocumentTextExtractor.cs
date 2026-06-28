using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Application.Models;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>
/// Service contract for extracting raw text from various document streams (PDF, DOCX, TXT, MD).
/// </summary>
public interface IDocumentTextExtractor
{
    Task<ExtractedDocumentContent> ExtractTextAsync(Stream fileStream, string fileExtension, CancellationToken ct = default);
}
