using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Application.LanguagePolicy;

public interface ILanguagePolicy
{
    Task<bool> IsSupportedAsync(string code);
    // IsAllowedToSpeak/IsAllowedToListen were removed with the room-language restriction
    // they existed to express — see LanguagePolicy.ValidateParticipantLanguagesAsync. A
    // predicate that no longer gates anything is worse than none: it reads as a rule
    // while enforcing nothing, which is precisely how the STT language filter came to be
    // silently unreachable.
    Task<string?> ValidateParticipantLanguagesAsync(string? speakLanguage, string? listenLanguage, TranslationRoom room);
    bool IsTranslationRequired(string speakLanguage, string listenLanguage);
}
