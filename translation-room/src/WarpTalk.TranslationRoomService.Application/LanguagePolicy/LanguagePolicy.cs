using System;
using System.Collections.Generic;
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
    /// 3. Room validation (the language is one this MEETING declares — its L2, restored by
    ///    WT-709; the comment at the bottom of this method explains what changed and why)
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

        // 3. The ROOM's own languages (L2): its source language plus its targets.
        //
        //    WT-709 puts this step back, and the comment it replaces argued at length for leaving
        //    it out. Both halves of that argument were answered rather than overruled, so they are
        //    worth recording:
        //
        //    "It did not actually hold" — true, and that was the real complaint. This policy
        //    gated the REST join while TranslationRoomHub.SetSpeakLanguage/SetListenLanguage, the
        //    way anybody actually changes language once they are in the room, never consulted it.
        //    A rule you can step around by changing your mind after you arrive is not a rule. It
        //    holds now: the hub asks IRoomLanguagePolicy on Set*Language AND on
        //    JoinTranslationRoom, and that predicate checks the room's languages alongside the
        //    workspace's. One rule, every door.
        //
        //    "It is the wrong rule" — it was, as long as the room's language set was frozen at
        //    booking time. A participant who needs Korean in a vi/en room was genuinely
        //    misconfigured-by-the-host, and refusing them was refusing the product's whole point.
        //    That is why WT-709 does not simply refuse: the HOST can add a language to the room
        //    from inside the running meeting (AddRoomLanguageAsync), still bounded by the
        //    workspace whitelist and the plan's quota. The answer to "this room does not speak
        //    Korean" is now "so add Korean", which takes one click and leaves a record.
        //
        //    What the open door cost, and why closing it matters: languages narrow L1 ⊇ L2 ⊇
        //    artifacts. A participant picking outside L2 wrote utterances into the meeting's
        //    transcript in a language the meeting never declared, so the stored record contained
        //    languages that are in no snapshot of what this meeting was — and the artifact rules
        //    (WT-703/704), which narrow FROM L2, then had nothing honest to narrow from. A
        //    language the host adds mid-meeting IS in L2 and travels with the room into its
        //    snapshot; a language nobody declared is a hole in the record.
        //
        //    An EMPTY set means "unknown", not "nothing allowed" — the same reading the workspace
        //    whitelist gets everywhere else. A room with no declared languages is a fixture or an
        //    external bridge, and refusing every join into one would be a regression with no rule
        //    behind it.
        var roomLanguages = new List<string> { Helpers.LanguageHelper.NormalizeLanguageCode(room.SourceLanguage) }
            .Concat(Helpers.LanguageHelper.ParseTargetLanguages(room.TargetLanguages))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (roomLanguages.Count > 0)
        {
            var declared = string.Join(", ", roomLanguages);

            if (!roomLanguages.Contains(speakLanguage, StringComparer.OrdinalIgnoreCase))
                return string.Format(
                    TranslationRoomConstants.ValidationLanguageNotAllowedByPolicy,
                    "Speak", speakLanguage, declared);

            if (!roomLanguages.Contains(listenLanguage, StringComparer.OrdinalIgnoreCase))
                return string.Format(
                    TranslationRoomConstants.ValidationLanguageNotAllowedByPolicy,
                    "Listen", listenLanguage, declared);
        }

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
