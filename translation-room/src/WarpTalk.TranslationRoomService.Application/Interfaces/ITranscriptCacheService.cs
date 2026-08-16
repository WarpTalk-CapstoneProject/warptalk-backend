using System.Collections.Generic;
using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface ITranscriptCacheService
{
    /// <summary>
    /// The cached transcript lines for a room, or an empty list when nothing is cached.
    ///
    /// WT-431: this used to be <c>AssembleTranscriptAsync</c>, which returned a fully formatted
    /// document — header, and the literal "*No speech transcription recorded.*" when the list was
    /// empty. That made "the cache is empty" and "nobody spoke" produce byte-identical output, so
    /// the finalizer could not tell them apart and neither could anyone reading the artifact
    /// afterwards. Returning the raw lines leaves that judgement to the one caller that has the
    /// context to make it.
    /// </summary>
    Task<IReadOnlyList<string>> ReadCachedSegmentsAsync(string redisKey);
}
