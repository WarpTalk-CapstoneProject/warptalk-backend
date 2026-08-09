using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface ISupportedLanguageService
{
    Task<IReadOnlyList<SupportedLanguageDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SupportedLanguageDto>> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<SupportedLanguageDto> CreateAsync(string code, string name, string? nativeName, CancellationToken cancellationToken = default);
    Task<SupportedLanguageDto> UpdateAsync(string code, string name, string? nativeName, CancellationToken cancellationToken = default);
    Task<SupportedLanguageDto> ToggleActiveAsync(string code, bool isActive, CancellationToken cancellationToken = default);
}
