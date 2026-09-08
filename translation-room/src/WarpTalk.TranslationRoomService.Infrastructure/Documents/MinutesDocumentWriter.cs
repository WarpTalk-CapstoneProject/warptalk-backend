using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.Infrastructure.Documents;

/// <summary>
/// Picks the layout and hands the work to it.
///
/// The two writers stay separate classes rather than one class with branches through every block:
/// the layouts differ in section order, numbering, date format and what belongs in a table, so a
/// shared body would be a chain of if-template checks with almost no shared line between them.
/// Keeping them apart is also what makes each one readable as the form it is imitating — a person
/// checking this against Nghị định 30 should be able to read one file top to bottom.
///
/// An unknown template renders the default rather than failing. See
/// <see cref="MinutesTemplates.Normalise"/>: refusing to hand somebody their own minutes because
/// a query string carried a typo trades a readable document for no document.
/// </summary>
public class MinutesDocumentWriter : IMeetingMinutesDocumentWriter
{
    private readonly MeetingMinutesDocxWriter _vietnamese = new();
    private readonly GlobalMinutesDocxWriter _global = new();

    public byte[] WriteDocx(MeetingMinutesDto minutes, MeetingMinutesContent content, string template)
        => MinutesTemplates.Normalise(template) == MinutesTemplates.VnNd30
            ? _vietnamese.WriteDocx(minutes, content)
            : _global.WriteDocx(minutes, content);
}
