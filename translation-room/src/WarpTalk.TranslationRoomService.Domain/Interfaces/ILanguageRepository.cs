using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface ILanguageRepository
{
    Task<bool> IsSupportedAsync(string code);

    /// <summary>
    /// The whole catalog, inactive rows included, ordered by code.
    ///
    /// Inactive rows are the point of this one existing separately from the public listing: an
    /// administrator asking "why can nobody create a Korean room" needs to see that Korean is
    /// present and switched off, not an absence they cannot distinguish from a missing row.
    /// </summary>
    Task<IReadOnlyList<SupportedLanguage>> GetCatalogAsync(CancellationToken ct = default);

    /// <summary>
    /// One catalog row, TRACKED, matched on the code case-insensitively — for an admin edit
    /// (WT-691). Saved by <c>IUnitOfWork.SaveChangesAsync</c>.
    /// </summary>
    Task<SupportedLanguage?> GetForUpdateAsync(string code, CancellationToken ct = default);

    /// <summary>Stages a new catalog row. Saved by <c>IUnitOfWork.SaveChangesAsync</c>.</summary>
    Task AddAsync(SupportedLanguage language, CancellationToken ct = default);
}
