using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Application.LanguagePolicy;

public interface ILanguagePolicy
{
    Task<bool> IsSupportedAsync(string code);
    bool IsAllowedToSpeak(string language, TranslationRoom room);
    bool IsAllowedToListen(string language, TranslationRoom room);
    Task<string?> ValidateParticipantLanguagesAsync(string? speakLanguage, string? listenLanguage, TranslationRoom room);
}
