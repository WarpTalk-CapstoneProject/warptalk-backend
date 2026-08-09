using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface ILanguageRepository
{
    Task<bool> IsSupportedAsync(string code);
    Task<System.Collections.Generic.IReadOnlyList<Entities.SupportedLanguage>> GetAllAsync(System.Threading.CancellationToken cancellationToken = default);
    Task<System.Collections.Generic.IReadOnlyList<Entities.SupportedLanguage>> GetActiveAsync(System.Threading.CancellationToken cancellationToken = default);
    Task<Entities.SupportedLanguage?> GetByCodeAsync(string code, System.Threading.CancellationToken cancellationToken = default);
    Task AddAsync(Entities.SupportedLanguage language, System.Threading.CancellationToken cancellationToken = default);
    Task UpdateAsync(Entities.SupportedLanguage language, System.Threading.CancellationToken cancellationToken = default);
}
