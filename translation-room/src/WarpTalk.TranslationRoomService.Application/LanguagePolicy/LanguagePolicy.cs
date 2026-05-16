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

    public bool IsAllowedToSpeak(string language, TranslationRoom room)
    {
        if (string.IsNullOrWhiteSpace(language) || room == null) return false;

        // Allowed to speak Source Language
        if (language.Equals(room.SourceLanguage, StringComparison.OrdinalIgnoreCase))
            return true;

        // Or any of the Target Languages
        var targets = Helpers.LanguageHelper.ParseTargetLanguages(room.TargetLanguages);
        return targets.Any(t => t.Equals(language, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsAllowedToListen(string language, TranslationRoom room)
    {
        if (string.IsNullOrWhiteSpace(language) || room == null) return false;

        // MUST be one of the Target Languages per practical rule
        var targets = Helpers.LanguageHelper.ParseTargetLanguages(room.TargetLanguages);
        return targets.Any(t => t.Equals(language, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Validates the participant's requested languages before joining a room.
    /// This includes 3 levels of validation:
    /// 1. Basic validation (Not null/empty)
    /// 2. System validation (Check if language is globally supported in the database)
    /// 3. Policy validation (Check if language is allowed in this specific room's configuration)
    /// </summary>
    public async Task<string?> ValidateParticipantLanguagesAsync(string? speakLanguage, string? listenLanguage, TranslationRoom room)
    {
        // 1. Basic format validation
        if (string.IsNullOrWhiteSpace(speakLanguage))
            return TranslationRoomConstants.ValidationSpeakLanguageRequired;
        
        if (string.IsNullOrWhiteSpace(listenLanguage))
            return TranslationRoomConstants.ValidationListenLanguageRequired;
            
        // 2. System-level validation: Ensure languages are supported by the platform
        if (!await IsSupportedAsync(speakLanguage))
            return string.Format(TranslationRoomConstants.ValidationLanguageUnsupported, speakLanguage);
            
        if (!await IsSupportedAsync(listenLanguage))
            return string.Format(TranslationRoomConstants.ValidationLanguageUnsupported, listenLanguage);
            
        // 3. Room-level policy validation: Ensure languages match the room's source/target config
        if (!IsAllowedToSpeak(speakLanguage, room))
            return string.Format(TranslationRoomConstants.ValidationLanguageNotAllowedByPolicy, "Speak", speakLanguage);
            
        if (!IsAllowedToListen(listenLanguage, room))
            return string.Format(TranslationRoomConstants.ValidationLanguageNotAllowedByPolicy, "Listen", listenLanguage);

        return null; // Null means no validation errors (Success)
    }
}
