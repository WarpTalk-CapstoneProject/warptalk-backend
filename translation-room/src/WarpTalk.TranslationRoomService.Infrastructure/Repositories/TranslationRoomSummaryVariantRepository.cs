using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class TranslationRoomSummaryVariantRepository
    : GenericRepository<TranslationRoomSummaryVariant>, ITranslationRoomSummaryVariantRepository
{
    public TranslationRoomSummaryVariantRepository(TranslationRoomDbContext context) : base(context)
    {
    }

    public async Task<TranslationRoomSummaryVariant?> GetAsync(
        Guid roomId, string templateKey, string language, CancellationToken ct = default)
    {
        // Compared exactly, never case-insensitively. Both halves are normalised before they
        // reach the database — the template key is lower-cased where the request is built, and
        // the language passes through LanguageHelper.NormalizeLanguageCode — so a case-folding
        // comparison here would only hide a caller that skipped one of them, and would not
        // match the unique index that decides where a write lands.
        return await _dbSet
            .FirstOrDefaultAsync(
                v => v.TranslationRoomId == roomId
                    && v.TemplateKey == templateKey
                    && v.Language == language,
                ct);
    }

    public async Task<List<TranslationRoomSummaryVariant>> GetByRoomIdAsync(
        Guid roomId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(v => v.TranslationRoomId == roomId)
            .OrderBy(v => v.CreatedAt)
            .ToListAsync(ct);
    }
}
