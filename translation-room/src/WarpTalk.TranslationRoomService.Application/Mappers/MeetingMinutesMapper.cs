using System;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Application.Mappers;

public static class MeetingMinutesMapper
{
    /// <summary>
    /// One biên bản as the web reads it.
    ///
    /// The name lookup is a parameter rather than a roster this method queries or filters, because
    /// the two callers resolve it differently and must not each restate the mapping: the
    /// single-document read asks for one room's roster, and the library read batches every room on
    /// the page into one dictionary rather than paying a query per row. Sharing the projection is
    /// what keeps a minutes document from describing itself one way in the library and another way
    /// when opened.
    ///
    /// <paramref name="nameOf"/> returns null for a participant it cannot name — the row was
    /// purged, or the signatory never joined the meeting they chaired. A placeholder invented here
    /// would appear on a signed record as if somebody had put it there.
    /// </summary>
    public static MeetingMinutesDto ToDto(this MeetingMinutes minutes, Func<Guid?, string?> nameOf)
        => new(
            minutes.Id,
            minutes.TranslationRoomId,
            minutes.MinutesNo,
            // Read out of the document body rather than stored beside it. Underneath, a biên bản
            // is identified by its number and nothing else — this is a display name, so it is
            // derived on the way out and never becomes a second thing that can disagree with the
            // document it names.
            //
            // Costs one small parse per row. The row already carries its whole Content over the
            // wire, so the parse is cheaper than the serialisation happening around it.
            MeetingMinutesDrafter.TitleFrom(minutes.Content),
            minutes.Status,
            minutes.Version,
            minutes.IsCurrent,
            minutes.PreviousMinutesId,
            minutes.BasedOnTranscriptVersion,
            minutes.DraftedByEngine,
            minutes.DraftedAt,
            minutes.SecretaryParticipantId,
            nameOf(minutes.SecretaryParticipantId),
            minutes.SecretarySignedAt,
            minutes.ChairParticipantId,
            nameOf(minutes.ChairParticipantId),
            minutes.ChairApprovedAt,
            minutes.EditCountVsDraft,
            minutes.Content,
            minutes.CreatedAt,
            minutes.UpdatedAt);
}
