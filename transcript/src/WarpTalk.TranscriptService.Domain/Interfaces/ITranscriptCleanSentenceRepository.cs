using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.Domain.Interfaces;

public interface ITranscriptCleanSentenceRepository : IGenericRepository<TranscriptCleanSentence>
{
    /// <summary>WT-716: every clean sentence of one transcript, unordered — conversation order is
    /// decided by the caller from the covered segments' sequence_order.</summary>
    Task<IReadOnlyList<TranscriptCleanSentence>> GetByTranscriptIdAsync(Guid transcriptId, CancellationToken cancellationToken = default);
}
