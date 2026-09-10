using WarpTalk.TranslationRoomService.Domain.Entities;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface ITranslationRoomSummaryVariantRepository : IGenericRepository<TranslationRoomSummaryVariant>
{
    /// <summary>
    /// The cached rendering for exactly this (shape, language), or null if nobody has asked for
    /// it yet — which is the signal to queue a generation rather than an error.
    /// </summary>
    Task<TranslationRoomSummaryVariant?> GetAsync(
        Guid roomId, string templateKey, string language, CancellationToken ct = default);

    /// <summary>
    /// Every rendering this room already has, so the picker can say which choices are instant
    /// and which will cost a wait. Ordered oldest first, which is the order they were asked for.
    /// </summary>
    Task<List<TranslationRoomSummaryVariant>> GetByRoomIdAsync(Guid roomId, CancellationToken ct = default);
}
