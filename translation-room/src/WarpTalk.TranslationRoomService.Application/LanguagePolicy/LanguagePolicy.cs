using System;
using System.Linq;
using System.Threading.Tasks;
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

    public async Task<string?> ValidateParticipantLanguagesAsync(string? speakLanguage, string? listenLanguage, TranslationRoom room)
    {
        if (string.IsNullOrWhiteSpace(speakLanguage))
            return Domain.Constants.TranslationRoomConstants.ValidationSpeakLanguageRequired;
        
        if (string.IsNullOrWhiteSpace(listenLanguage))
            return Domain.Constants.TranslationRoomConstants.ValidationListenLanguageRequired;
            
        if (!await IsSupportedAsync(speakLanguage))
            return string.Format(Domain.Constants.TranslationRoomConstants.ValidationLanguageUnsupported, speakLanguage);
            
        if (!await IsSupportedAsync(listenLanguage))
            return string.Format(Domain.Constants.TranslationRoomConstants.ValidationLanguageUnsupported, listenLanguage);
            
        if (!IsAllowedToSpeak(speakLanguage, room))
            return string.Format(Domain.Constants.TranslationRoomConstants.ValidationLanguageNotAllowedByPolicy, "Speak", speakLanguage);
            
        if (!IsAllowedToListen(listenLanguage, room))
            return string.Format(Domain.Constants.TranslationRoomConstants.ValidationLanguageNotAllowedByPolicy, "Listen", listenLanguage);

        return null;
    }
}
