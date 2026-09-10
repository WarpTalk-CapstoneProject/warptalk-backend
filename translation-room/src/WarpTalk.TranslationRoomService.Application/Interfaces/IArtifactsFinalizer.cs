using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface IArtifactsFinalizer
{
    /// <summary>
    /// Draw up this room's artifacts.
    ///
    /// <paramref name="templateKey"/> and <paramref name="summaryLanguage"/> are non-null only
    /// when a person asked for a specific summary and this finalization is what their request
    /// was redirected into — see <see cref="FinalizationRequest"/>. Null is the ordinary case and
    /// keeps the default shape.
    /// </summary>
    Task ProcessRoomFinalizationAsync(
        Guid roomId,
        string? templateKey = null,
        string? summaryLanguage = null,
        CancellationToken ct = default);
}
