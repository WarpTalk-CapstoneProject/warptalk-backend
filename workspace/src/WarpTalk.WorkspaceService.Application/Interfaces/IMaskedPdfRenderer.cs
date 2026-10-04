using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>
/// Renders a PDF from an already-masked Word document.
/// </summary>
/// <remarks>
/// A PDF cannot be masked in place with what this solution has: the library it reads PDFs with
/// (iTextSharp LGPL) can draw over text but not remove it from the content stream, and a box
/// drawn over a name is not redaction — the name is still selectable underneath. So a PDF's
/// masked copy is a NEW pdf, laid out from the masked text. Its layout is not the original's.
///
/// Null means "no PDF": not configured, refused, timed out. The caller stores nothing.
/// </remarks>
public interface IMaskedPdfRenderer
{
    bool IsConfigured { get; }

    Task<byte[]?> RenderFromDocxAsync(byte[] docx, CancellationToken ct = default);
}
