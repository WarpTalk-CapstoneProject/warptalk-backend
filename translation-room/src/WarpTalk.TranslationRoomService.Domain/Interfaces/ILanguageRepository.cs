using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface ILanguageRepository
{
    Task<bool> IsSupportedAsync(string code);
}
