using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Application.LanguagePolicy;

public interface ILanguagePolicy
{
    Task<bool> IsSupportedAsync(string code);
    // IsAllowedToSpeak/IsAllowedToListen stay removed even though WT-709 restored the rule they
    // expressed. The restriction is one decision over a pair of languages, taken in one place
    // (ValidateParticipantLanguagesAsync) and enforced on every door into a room; two public
    // predicates beside it are two more things a caller can forget to ask, which is exactly how
    // the room check came to gate the REST join and nothing else.
    Task<string?> ValidateParticipantLanguagesAsync(string? speakLanguage, string? listenLanguage, TranslationRoom room);
    bool IsTranslationRequired(string speakLanguage, string listenLanguage);
}
