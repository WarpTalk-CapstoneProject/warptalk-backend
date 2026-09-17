using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

/// <inheritdoc cref="IRoomArtifactLanguagePolicy" />
public class RoomArtifactLanguagePolicy : IRoomArtifactLanguagePolicy
{
    // A normalized code is the primary subtag only; anything else is not a language code.
    private static readonly Regex LanguageCodePattern = new("^[a-z]{2,3}$", RegexOptions.CultureInvariant);

    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceMeetingPolicy _workspaceMeetingPolicy;
    private readonly ILogger<RoomArtifactLanguagePolicy> _logger;

    public RoomArtifactLanguagePolicy(
        IUnitOfWork unitOfWork,
        IWorkspaceMeetingPolicy workspaceMeetingPolicy,
        ILogger<RoomArtifactLanguagePolicy> logger)
    {
        _unitOfWork = unitOfWork;
        _workspaceMeetingPolicy = workspaceMeetingPolicy;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetGeneratableLanguagesAsync(
        TranslationRoom room,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(room);

        // L2: built exactly as GetJoinLanguagePolicyByCodeAsync builds it, so the join screen and
        // the artifacts can never disagree about what "this room's languages" means.
        IEnumerable<string> languages = new List<string> { LanguageHelper.NormalizeLanguageCode(room.SourceLanguage) }
            .Concat(LanguageHelper.ParseTargetLanguages(room.TargetLanguages))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var workspaceAllowed = await GetWorkspaceAllowedLanguagesAsync(room, ct);
        if (workspaceAllowed.Count > 0)
            languages = languages.Where(workspaceAllowed.Contains);

        var catalog = await GetActiveCatalogAsync(room, ct);
        if (catalog.Count > 0)
            languages = languages.Where(catalog.Contains);

        return languages.ToList();
    }

    /// <inheritdoc />
    public async Task<Result> EnsureCanGenerateAsync(
        TranslationRoom room,
        string? requestedLanguage,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(room);

        // As spoken: the content follows the transcript, so no new language is being produced.
        if (string.IsNullOrWhiteSpace(requestedLanguage))
            return Result.Success();

        var normalized = LanguageHelper.NormalizeLanguageCode(requestedLanguage);
        var generatable = await GetGeneratableLanguagesAsync(room, ct);

        if (LanguageCodePattern.IsMatch(normalized) &&
            generatable.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            return Result.Success();

        var allowed = generatable.Count > 0 ? string.Join(", ", generatable) : "none";
        return Result.Failure(
            $"Language '{requestedLanguage.Trim()}' is not allowed for this meeting's artifacts. Allowed languages: {allowed}.",
            ErrorCodes.ValidationError);
    }

    /// <summary>
    /// L1, normalized. Empty means unrestricted, and is also what every failure answers with:
    /// the room's own set was validated against this whitelist when it was saved, so falling
    /// back to it neither widens past the owner's rule nor refuses everything over a blip.
    /// </summary>
    private async Task<HashSet<string>> GetWorkspaceAllowedLanguagesAsync(TranslationRoom room, CancellationToken ct)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (room.WorkspaceId == Guid.Empty)
            return allowed;

        try
        {
            var result = await _workspaceMeetingPolicy.GetAllowedLanguagesAsync(room.WorkspaceId, ct);
            if (!result.IsSuccess || result.Value == null)
            {
                _logger.LogWarning(
                    "Workspace language policy lookup failed for workspace {WorkspaceId} (room {RoomId}): {Error}. Falling back to the room's own languages.",
                    room.WorkspaceId, room.Id, result.Error);
                return allowed;
            }

            foreach (var code in result.Value.Select(LanguageHelper.NormalizeLanguageCode))
            {
                if (!string.IsNullOrEmpty(code)) allowed.Add(code);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Workspace language policy lookup threw for workspace {WorkspaceId} (room {RoomId}). Falling back to the room's own languages.",
                room.WorkspaceId, room.Id);
        }

        return allowed;
    }

    /// <summary>
    /// Active catalog codes, normalized (the catalog is seeded with locale tags such as 'vi-VN').
    /// Empty when the catalog cannot be read or holds no active row — the caller skips the filter.
    /// </summary>
    private async Task<HashSet<string>> GetActiveCatalogAsync(TranslationRoom room, CancellationToken ct)
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var catalog = await _unitOfWork.LanguageRepository.GetCatalogAsync(ct);
            foreach (var language in catalog ?? Array.Empty<SupportedLanguage>())
            {
                if (!language.IsActive) continue;
                var code = LanguageHelper.NormalizeLanguageCode(language.Code);
                if (!string.IsNullOrEmpty(code)) active.Add(code);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Language catalog read failed while resolving artifact languages for room {RoomId}. Skipping the catalog filter.",
                room.Id);
            return active;
        }

        if (active.Count == 0)
            _logger.LogWarning(
                "Language catalog has no active rows while resolving artifact languages for room {RoomId}. Skipping the catalog filter.",
                room.Id);

        return active;
    }
}
