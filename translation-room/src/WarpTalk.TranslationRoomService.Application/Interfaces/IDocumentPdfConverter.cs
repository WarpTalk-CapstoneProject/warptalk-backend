using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// Turns the .docx this service already produces into a PDF.
///
/// WHY CONVERT RATHER THAN RENDER A SECOND TIME
///     A PDF writer built beside <see cref="IMeetingMinutesDocumentWriter"/> would be a second
///     implementation of the same document: two layouts, two sets of margins, two places to fix
///     a numbering bug. They would agree the day they were written and drift from then on, and a
///     signed record whose Word copy and PDF copy differ is worse than having no PDF. So the DOCX
///     stays the single original, and the PDF is a rendering of it.
///
/// WHY IT IS ALLOWED TO BE ABSENT
///     Conversion needs an office engine, which is a separate process a deployment may not have.
///     <see cref="IsConfigured"/> lets the product say "PDF is not available here" instead of
///     failing at the moment a person presses the button.
/// </summary>
public interface IDocumentPdfConverter
{
    /// <summary>Whether this deployment has somewhere to convert.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// The PDF, or null when conversion was not possible. Null rather than an exception because a
    /// failed conversion is an ordinary outcome the caller must turn into "the Word file still
    /// downloads", not an error worth unwinding a request for.
    /// </summary>
    Task<byte[]?> ToPdfAsync(byte[] docx, string fileName, CancellationToken ct = default);
}
