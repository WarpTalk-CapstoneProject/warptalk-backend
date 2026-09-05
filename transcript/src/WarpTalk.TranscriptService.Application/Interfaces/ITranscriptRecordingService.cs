using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.DTOs;

namespace WarpTalk.TranscriptService.Application.Interfaces;

/// <summary>
/// WT-605. Pause/Resume Transcript — stop the transcript from being written down while
/// translation, dubbing, subtitles and LiveKit keep running untouched.
///
/// Deliberately separate from ITranslationRoomService's Pause/Resume/Stop Translation:
/// those gate AudioRoutingEventType (room_pause/translation_stopped), which the AI workers
/// treat as "stop listening" — exactly what this feature must NOT do.
/// </summary>
public interface ITranscriptRecordingService
{
    Task<Result> PauseAsync(Guid translationRoomId, Guid callerId, CancellationToken cancellationToken = default);
    Task<Result> ResumeAsync(Guid translationRoomId, Guid callerId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<TranscriptPauseWindowDto>>> GetPauseWindowsAsync(Guid translationRoomId, Guid callerId, CancellationToken cancellationToken = default);
}
