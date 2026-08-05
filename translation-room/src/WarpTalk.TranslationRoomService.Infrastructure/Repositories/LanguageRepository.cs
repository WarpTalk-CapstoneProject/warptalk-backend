using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class LanguageRepository : ILanguageRepository
{
    private readonly TranslationRoomDbContext _dbContext;

    public LanguageRepository(TranslationRoomDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Whether the platform supports a language, regardless of which spelling either side uses.
    ///
    /// This lookup sits where two keyings meet, and they have never agreed. The catalog
    /// (translation_room.supported_languages, this service's own view over the
    /// platform-owned table) is seeded with locale tags — 'en-US', 'vi-VN' — by
    /// 20260730141000_seed_supported_languages.sql, while callers hand us the bare ISO-639-1
    /// code, because TranslationRoomService and LanguagePolicy both run every value through
    /// LanguageHelper.NormalizeLanguageCode first. An exact match therefore could not hit, and
    /// creating, updating or joining a room all failed with "Source language is not supported."
    ///
    /// Matching on the primary subtag fixes all three at once and, unlike re-seeding the
    /// catalog, cannot break again if it is ever re-seeded in the other spelling — which is
    /// exactly how this regressed: the previous fix changed the data instead of the comparison,
    /// leaving the format an unwritten contract that the next change silently violated.
    /// </summary>
    public async Task<bool> IsSupportedAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;

        // "en-US" and "en" both reduce to "en"; a catalog row of either shape must match both.
        var exact = code.Trim().ToLowerInvariant();
        var primary = LanguageHelper.NormalizeLanguageCode(code);
        if (string.IsNullOrEmpty(primary)) return false;

        // Guards against a stray "-" matching every row through the StartsWith below.
        var regionPrefix = primary + "-";

        return await _dbContext.SupportedLanguages
            .AsNoTracking()
            .AnyAsync(language => language.IsActive &&
                (language.Code.ToLower() == exact ||
                 language.Code.ToLower() == primary ||
                 language.Code.ToLower().StartsWith(regionPrefix)));
    }
}
