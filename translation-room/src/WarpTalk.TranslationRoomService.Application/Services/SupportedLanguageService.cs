using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.TranslationRoomService.Application.Services;

public class SupportedLanguageService : ISupportedLanguageService
{
    private readonly ILanguageRepository _languageRepository;

    public SupportedLanguageService(ILanguageRepository languageRepository)
    {
        _languageRepository = languageRepository;
    }

    public async Task<IReadOnlyList<SupportedLanguageDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var languages = await _languageRepository.GetAllAsync(cancellationToken);
        return languages.Select(MapToDto).ToList();
    }

    public async Task<IReadOnlyList<SupportedLanguageDto>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        var languages = await _languageRepository.GetActiveAsync(cancellationToken);
        return languages.Select(MapToDto).ToList();
    }

    public async Task<SupportedLanguageDto> CreateAsync(string code, string name, string? nativeName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code is required");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required");

        var existing = await _languageRepository.GetByCodeAsync(code, cancellationToken);
        if (existing != null)
        {
            throw new InvalidOperationException($"Language with code '{code}' already exists.");
        }

        var language = new SupportedLanguage
        {
            Code = code.Trim(),
            Name = name.Trim(),
            NativeName = nativeName?.Trim(),
            IsActive = true
        };

        await _languageRepository.AddAsync(language, cancellationToken);
        return MapToDto(language);
    }

    public async Task<SupportedLanguageDto> UpdateAsync(string code, string name, string? nativeName, CancellationToken cancellationToken = default)
    {
        var language = await _languageRepository.GetByCodeAsync(code, cancellationToken);
        if (language == null)
        {
            throw new InvalidOperationException($"Language with code '{code}' not found.");
        }

        language.Name = name.Trim();
        language.NativeName = nativeName?.Trim();

        await _languageRepository.UpdateAsync(language, cancellationToken);
        return MapToDto(language);
    }

    public async Task<SupportedLanguageDto> ToggleActiveAsync(string code, bool isActive, CancellationToken cancellationToken = default)
    {
        var language = await _languageRepository.GetByCodeAsync(code, cancellationToken);
        if (language == null)
        {
            throw new InvalidOperationException($"Language with code '{code}' not found.");
        }

        language.IsActive = isActive;
        await _languageRepository.UpdateAsync(language, cancellationToken);
        return MapToDto(language);
    }

    private static SupportedLanguageDto MapToDto(SupportedLanguage entity)
    {
        return new SupportedLanguageDto(entity.Code, entity.Name, entity.NativeName, entity.IsActive);
    }
}
