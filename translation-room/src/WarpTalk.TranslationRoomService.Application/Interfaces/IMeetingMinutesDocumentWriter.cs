using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// Renders a biên bản as the file somebody actually sends.
///
/// WHY THE SERVER MAKES THE FILE
///     Two reasons, and the second is the one that matters. A .docx is a zip of XML parts, so
///     building it in the browser means shipping a document library to every visitor to produce
///     something only the host ever asks for. And an APPROVED minutes must render identically
///     whoever asks for it — a client-side renderer would make the signed document a function of
///     the reader's browser version.
///
/// WHY .docx AND NOT PDF HERE
///     A biên bản in Vietnamese practice gets emailed, and the recipient adds their own header,
///     stamps it, or pastes it into a longer report. That wants an editable document. PDF is a
///     print of this same document, produced from the reader's own print dialog against the
///     minutes page — which costs no second document library and is exactly as faithful.
/// </summary>
public interface IMeetingMinutesDocumentWriter
{
    /// <summary>
    /// The .docx bytes for one minutes document.
    ///
    /// Takes the parsed content rather than the raw JSON so that a document which fails to parse
    /// cannot reach the renderer at all — it would otherwise produce a file with a number, a
    /// signature block, and nothing in between.
    /// </summary>
    byte[] WriteDocx(MeetingMinutesDto minutes, MeetingMinutesContent content);
}
