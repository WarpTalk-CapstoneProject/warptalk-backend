using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;
using WarpTalk.TranslationRoomService.Application.DTOs.Admin;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

/// <inheritdoc cref="IAdminLanguageService"/>
public sealed partial class AdminLanguageService : IAdminLanguageService
{
    private const int MaxNameLength = 100;

    private static readonly HashSet<string> LiveStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(RoomStatus.IN_PROGRESS),
        nameof(RoomStatus.PAUSED),
        nameof(RoomStatus.WAITING),
    };

    private readonly IUnitOfWork _unitOfWork;
    private readonly IAdminAuditRecorder _audit;
    private readonly ILogger<AdminLanguageService> _logger;

    public AdminLanguageService(
        IUnitOfWork unitOfWork,
        IAdminAuditRecorder audit,
        ILogger<AdminLanguageService> logger)
    {
        _unitOfWork = unitOfWork;
        _audit = audit;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<AdminLanguageDto>>> GetCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            var catalog = await _unitOfWork.LanguageRepository.GetCatalogAsync(ct);
            var usage = await ReadUsageAsync(ct);
            return Result.Success<IReadOnlyList<AdminLanguageDto>>(
                catalog.Select(language => ToDto(language, usage)).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin language catalog read failed.");
            return Result.Failure<IReadOnlyList<AdminLanguageDto>>(
                "An unexpected error occurred while reading the language catalog.",
                ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<AdminLanguageDto>> CreateAsync(
        AdminCreateLanguageRequest request, AdminActorContext actor, CancellationToken ct = default)
    {
        if (!TryNormalizeCode(request.Code, out var code))
        {
            return Result.Failure<AdminLanguageDto>(
                "Language code must look like 'ko' or 'ko-KR' (ISO 639 language, optional ISO 3166 region).",
                ErrorCodes.ValidationError);
        }

        if (ValidateNames(request.Name, request.NativeName) is { } nameError)
        {
            return Result.Failure<AdminLanguageDto>(nameError, ErrorCodes.ValidationError);
        }

        try
        {
            var catalog = await _unitOfWork.LanguageRepository.GetCatalogAsync(ct);
            var primary = LanguageHelper.NormalizeLanguageCode(code);

            // Room validation matches on the primary subtag ("en" and "en-GB" are the same language to
            // it), so a second row for the same primary would be indistinguishable in every check —
            // and switching one of the two off would change nothing.
            var clash = catalog.FirstOrDefault(language =>
                LanguageHelper.NormalizeLanguageCode(language.Code) == primary);
            if (clash is not null)
            {
                return Result.Failure<AdminLanguageDto>(
                    string.Equals(clash.Code, code, StringComparison.OrdinalIgnoreCase)
                        ? $"'{clash.Code}' is already in the catalog."
                        : $"'{code}' is already covered by '{clash.Code}': rooms match languages on '{primary}', not on the region.",
                    ErrorCodes.Conflict);
            }

            var language = new SupportedLanguage
            {
                Code = code,
                Name = request.Name!.Trim(),
                NativeName = NullIfBlank(request.NativeName),
                IsActive = request.IsActive ?? true,
            };

            var recorded = await _audit.RecordAsync(
                AdminAuditLanguageActions.Created,
                AdminAuditEntityTypes.SupportedLanguage,
                actor.ActorId,
                $"Added {language.Code} ({language.Name}) to the language catalog",
                actor.CorrelationId,
                beforeSummary: null,
                afterSummary: Summary(language),
                ct);
            if (!recorded.IsSuccess)
            {
                return Result.Failure<AdminLanguageDto>(recorded.Error!, recorded.ErrorCode!);
            }

            await _unitOfWork.LanguageRepository.AddAsync(language, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(ToDto(language, await ReadUsageAsync(ct)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin language create failed. Code: {Code}", code);
            return Result.Failure<AdminLanguageDto>(
                "An unexpected error occurred while adding the language.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<AdminLanguageDto>> UpdateAsync(
        string code, AdminUpdateLanguageRequest request, AdminActorContext actor, CancellationToken ct = default)
    {
        if (ValidateNames(request.Name, request.NativeName) is { } nameError)
        {
            return Result.Failure<AdminLanguageDto>(nameError, ErrorCodes.ValidationError);
        }

        try
        {
            var language = await _unitOfWork.LanguageRepository.GetForUpdateAsync(code, ct);
            if (language is null)
            {
                return NotFound(code);
            }

            var before = Summary(language);
            var name = request.Name!.Trim();
            var nativeName = NullIfBlank(request.NativeName);
            if (language.Name == name && language.NativeName == nativeName)
            {
                // Nothing to record and nothing to save: an unchanged form is not an audit event.
                return Result.Success(ToDto(language, await ReadUsageAsync(ct)));
            }

            language.Name = name;
            language.NativeName = nativeName;

            var recorded = await _audit.RecordAsync(
                AdminAuditLanguageActions.Updated,
                AdminAuditEntityTypes.SupportedLanguage,
                actor.ActorId,
                $"Renamed catalog language {language.Code}",
                actor.CorrelationId,
                before,
                Summary(language),
                ct);
            if (!recorded.IsSuccess)
            {
                return Result.Failure<AdminLanguageDto>(recorded.Error!, recorded.ErrorCode!);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success(ToDto(language, await ReadUsageAsync(ct)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin language update failed. Code: {Code}", code);
            return Result.Failure<AdminLanguageDto>(
                "An unexpected error occurred while updating the language.", ErrorCodes.InternalServerError);
        }
    }

    public Task<Result<AdminLanguageDto>> EnableAsync(string code, AdminActorContext actor, CancellationToken ct = default)
        => SetActiveAsync(code, isActive: true, confirmUpcoming: false, actor, ct);

    public Task<Result<AdminLanguageDto>> DisableAsync(
        string code, AdminDisableLanguageRequest request, AdminActorContext actor, CancellationToken ct = default)
        => SetActiveAsync(code, isActive: false, request.ConfirmUpcoming, actor, ct);

    private async Task<Result<AdminLanguageDto>> SetActiveAsync(
        string code, bool isActive, bool confirmUpcoming, AdminActorContext actor, CancellationToken ct)
    {
        try
        {
            var language = await _unitOfWork.LanguageRepository.GetForUpdateAsync(code, ct);
            if (language is null)
            {
                return NotFound(code);
            }

            var usage = await ReadUsageAsync(ct);
            if (language.IsActive == isActive)
            {
                return Result.Success(ToDto(language, usage));
            }

            if (!isActive)
            {
                var (live, upcoming) = UsageOf(language.Code, usage);

                // Refused outright: people are in these rooms now, and a participant who rejoins or
                // changes language would be told their language is not supported mid-meeting.
                if (live > 0)
                {
                    return Result.Failure<AdminLanguageDto>(
                        $"{language.Name} is in use by {live} live meeting(s). Disable it after they end.",
                        ErrorCodes.InvalidState);
                }

                // Allowed, but only knowingly: those rooms will be refused when someone edits or
                // joins them in this language.
                if (upcoming > 0 && !confirmUpcoming)
                {
                    return Result.Failure<AdminLanguageDto>(
                        $"{language.Name} is used by {upcoming} scheduled meeting(s). Confirm to disable it anyway.",
                        ErrorCodes.Conflict);
                }

                var catalog = await _unitOfWork.LanguageRepository.GetCatalogAsync(ct);
                if (catalog.Count(l => l.IsActive) <= 1)
                {
                    return Result.Failure<AdminLanguageDto>(
                        "At least one language must stay enabled, or no room could be created at all.",
                        ErrorCodes.InvalidState);
                }
            }

            var before = Summary(language);
            language.IsActive = isActive;

            var recorded = await _audit.RecordAsync(
                isActive ? AdminAuditLanguageActions.Enabled : AdminAuditLanguageActions.Disabled,
                AdminAuditEntityTypes.SupportedLanguage,
                actor.ActorId,
                isActive
                    ? $"Enabled {language.Code} for new rooms"
                    : $"Disabled {language.Code} for new rooms",
                actor.CorrelationId,
                before,
                Summary(language),
                ct);
            if (!recorded.IsSuccess)
            {
                return Result.Failure<AdminLanguageDto>(recorded.Error!, recorded.ErrorCode!);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success(ToDto(language, usage));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin language switch failed. Code: {Code}, Active: {Active}", code, isActive);
            return Result.Failure<AdminLanguageDto>(
                "An unexpected error occurred while changing the language.", ErrorCodes.InternalServerError);
        }
    }

    /// <summary>Live and upcoming room counts per primary subtag ("ko", not "ko-KR").</summary>
    private async Task<Dictionary<string, (int Live, int Upcoming)>> ReadUsageAsync(CancellationToken ct)
    {
        var rooms = await _unitOfWork.TranslationRoomRepository.GetOpenRoomLanguagesAsync(ct);
        var usage = new Dictionary<string, (int Live, int Upcoming)>(StringComparer.Ordinal);

        foreach (var room in rooms)
        {
            var languages = new HashSet<string>(StringComparer.Ordinal)
            {
                LanguageHelper.NormalizeLanguageCode(room.SourceLanguage),
            };
            try
            {
                languages.UnionWith(LanguageHelper.ParseTargetLanguages(room.TargetLanguages));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                // One malformed row must not blank the whole catalog; its source language still counts.
                _logger.LogWarning(ex, "Unreadable target_languages on an open room; counting its source only.");
            }

            var isLive = LiveStatuses.Contains(room.Status);
            foreach (var language in languages.Where(l => l.Length > 0))
            {
                var (live, upcoming) = usage.GetValueOrDefault(language);
                usage[language] = isLive ? (live + 1, upcoming) : (live, upcoming + 1);
            }
        }

        return usage;
    }

    private static (int Live, int Upcoming) UsageOf(string code, Dictionary<string, (int Live, int Upcoming)> usage)
        => usage.GetValueOrDefault(LanguageHelper.NormalizeLanguageCode(code));

    private static AdminLanguageDto ToDto(SupportedLanguage language, Dictionary<string, (int Live, int Upcoming)> usage)
    {
        var (live, upcoming) = UsageOf(language.Code, usage);
        return new AdminLanguageDto(language.Code, language.Name, language.NativeName, language.IsActive, live, upcoming);
    }

    private static Dictionary<string, string?> Summary(SupportedLanguage language) => new()
    {
        ["code"] = language.Code,
        ["name"] = language.Name,
        ["native_name"] = language.NativeName,
        ["is_active"] = language.IsActive ? "true" : "false",
    };

    private static Result<AdminLanguageDto> NotFound(string code) =>
        Result.Failure<AdminLanguageDto>($"'{code}' is not in the language catalog.", ErrorCodes.NotFound);

    private static string? ValidateNames(string? name, string? nativeName)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Name is required.";
        if (name.Trim().Length > MaxNameLength) return $"Name must be at most {MaxNameLength} characters.";
        if (nativeName is not null && nativeName.Trim().Length > MaxNameLength)
            return $"Native name must be at most {MaxNameLength} characters.";
        return null;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// "KO", "ko_kr", " ko-KR " → "ko" / "ko-KR": lower-case language, upper-case region, the same
    /// shape the seeded rows use. Anything else — scripts, private-use tags, free text — is refused:
    /// the catalog code is a key every service matches on.
    /// </summary>
    public static bool TryNormalizeCode(string? raw, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var match = CodePattern().Match(raw.Trim());
        if (!match.Success) return false;

        var language = match.Groups["lang"].Value.ToLowerInvariant();
        code = match.Groups["region"].Success
            ? $"{language}-{match.Groups["region"].Value.ToUpperInvariant()}"
            : language;
        return true;
    }

    [GeneratedRegex(@"^(?<lang>[A-Za-z]{2,3})(?:[-_](?<region>[A-Za-z]{2}))?$")]
    private static partial Regex CodePattern();
}
