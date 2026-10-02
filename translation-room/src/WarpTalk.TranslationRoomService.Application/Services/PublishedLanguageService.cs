using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

/// <summary>
/// WT-880: the languages a platform admin has published — the ENABLED rows of
/// <c>translation_room.supported_languages</c> (WT-691's catalog), for any signed-in user.
///
/// The glossary "Import template" tab offers these as the language pair to build a file for
/// (PO decision 2026-10-02: every language the admin has published, not the workspace's policy).
/// The admin catalog endpoint cannot serve that: it is staff-only and includes disabled rows.
/// </summary>
public interface IPublishedLanguageService
{
    Task<Result<IReadOnlyList<SupportedLanguageDto>>> GetPublishedAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IPublishedLanguageService"/>
public sealed class PublishedLanguageService : IPublishedLanguageService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<PublishedLanguageService> _logger;

    public PublishedLanguageService(IUnitOfWork unitOfWork, ILogger<PublishedLanguageService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<SupportedLanguageDto>>> GetPublishedAsync(CancellationToken ct = default)
    {
        try
        {
            var catalog = await _unitOfWork.LanguageRepository.GetCatalogAsync(ct);
            return Result.Success<IReadOnlyList<SupportedLanguageDto>>(catalog
                .Where(language => language.IsActive)
                .Select(language => LanguageMapper.ToDto(language.Code, language.Name, language.NativeName, language.IsActive))
                .ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Published language list read failed.");
            return Result.Failure<IReadOnlyList<SupportedLanguageDto>>(
                "An unexpected error occurred while reading the language list.",
                ErrorCodes.InternalServerError);
        }
    }
}
