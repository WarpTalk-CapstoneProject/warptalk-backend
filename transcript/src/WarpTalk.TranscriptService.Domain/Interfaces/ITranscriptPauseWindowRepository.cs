using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.Domain.Interfaces;

public interface ITranscriptPauseWindowRepository : IGenericRepository<TranscriptPauseWindow>
{
    /// <summary>The open window for this room (EndedAt == null), if any.</summary>
    Task<TranscriptPauseWindow?> GetActiveWindowByRoomIdAsync(Guid translationRoomId, CancellationToken cancellationToken = default);

    /// <summary>Every window for this room, oldest first — for the transcript panel's dividers.</summary>
    Task<IReadOnlyList<TranscriptPauseWindow>> GetWindowsByRoomIdAsync(Guid translationRoomId, CancellationToken cancellationToken = default);
}
