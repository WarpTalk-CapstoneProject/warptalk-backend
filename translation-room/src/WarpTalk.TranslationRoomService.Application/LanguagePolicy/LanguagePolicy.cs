using System;
using System.Linq;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.LanguagePolicy;

public class LanguagePolicy : ILanguagePolicy
{
    private readonly IUnitOfWork _unitOfWork;

    public LanguagePolicy(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<bool> IsSupportedAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        return await _unitOfWork.LanguageRepository.IsSupportedAsync(code);
    }

    /// <summary>
    /// Validates the participant's requested languages before joining a room.
    /// 1. Basic validation (not null/empty)
    /// 2. System validation (the platform supports this language at all)
    /// A third level — "is this language in the room's configured set" — was removed; the
    /// comment at the bottom of this method explains why, and it is worth reading before
    /// anyone adds it back.
    /// </summary>
    public async Task<string?> ValidateParticipantLanguagesAsync(string? speakLanguage, string? listenLanguage, TranslationRoom room)
    {
        // 1. Basic format validation
        if (string.IsNullOrWhiteSpace(speakLanguage))
            return TranslationRoomConstants.ValidationSpeakLanguageRequired;

        if (string.IsNullOrWhiteSpace(listenLanguage))
            return TranslationRoomConstants.ValidationListenLanguageRequired;

        speakLanguage = Helpers.LanguageHelper.NormalizeLanguageCode(speakLanguage);
        listenLanguage = Helpers.LanguageHelper.NormalizeLanguageCode(listenLanguage);

        // 2. System-level validation: Ensure languages are supported by the platform
        if (!await IsSupportedAsync(speakLanguage))
            return string.Format(TranslationRoomConstants.ValidationLanguageUnsupported, speakLanguage);

        if (!await IsSupportedAsync(listenLanguage))
            return string.Format(TranslationRoomConstants.ValidationLanguageUnsupported, listenLanguage);

        // 3. There is no third step any more, and the absence is deliberate.
        //
        //    This used to reject any language outside the room's configured source/target
        //    set. Two problems with that:
        //
        //    It did not actually hold. TranslationRoomHub.SetSpeakLanguage and
        //    SetListenLanguage — how anyone changes language once they are in the room —
        //    have never consulted this policy at all. So the restriction applied to joining
        //    and not to being here, and the only thing it reliably produced was a room you
        //    could sit in speaking Korean but could not REJOIN speaking Korean.
        //
        //    And it is the wrong rule. The room's languages are chosen by whoever booked
        //    the meeting, before they know who will turn up. A participant who speaks a
        //    language the host did not list is not misconfigured; they are a person who
        //    needs translating, which is what this product is for.
        //
        //    The configured set still decides what the UI OFFERS first — see the meeting
        //    control bar, where the room's languages are the list and everything else is
        //    behind "Add another language". What it no longer does is refuse.
        //
        //    Step 2 above still stands: a language must be one the platform actually
        //    supports. Cost follows usage, so a participant adding a language adds
        //    translation and dubbing work for their own routes — that is the same trade the
        //    host makes when configuring one, and it is bounded by who is in the room.
        return null; // Null means no validation errors (Success)
    }

    public bool IsTranslationRequired(string speakLanguage, string listenLanguage)
    {
        if (string.IsNullOrWhiteSpace(speakLanguage) || string.IsNullOrWhiteSpace(listenLanguage))
            return false;

        speakLanguage = Helpers.LanguageHelper.NormalizeLanguageCode(speakLanguage);
        listenLanguage = Helpers.LanguageHelper.NormalizeLanguageCode(listenLanguage);
        return !speakLanguage.Equals(listenLanguage, StringComparison.OrdinalIgnoreCase);
    }
}
