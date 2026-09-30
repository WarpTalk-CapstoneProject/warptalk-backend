using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

/// <inheritdoc cref="IMediaUsageService"/>
public sealed class MediaUsageService : IMediaUsageService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;

    public MediaUsageService(IUnitOfWork unitOfWork, TimeProvider? time = null)
    {
        _unitOfWork = unitOfWork;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Result<IReadOnlyList<MediaUsageCalculator.HourRow>>> GetHourlyAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        from = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        to = DateTime.SpecifyKind(to, DateTimeKind.Utc);
        if (to <= from || to - from > TimeSpan.FromDays(IMediaUsageService.MaxWindowDays))
        {
            return Result.Failure<IReadOnlyList<MediaUsageCalculator.HourRow>>(
                $"the window must be non-empty and at most {IMediaUsageService.MaxWindowDays} days", ErrorCodes.ValidationError);
        }

        var rooms = await _unitOfWork.TranslationRoomRepository.GetMediaUsageRoomsAsync(from, to, ct);
        var participants = await _unitOfWork.TranslationRoomParticipantRepository.GetMediaUsageParticipantsAsync(
            rooms.Select(room => room.RoomId).ToList(), ct);
        var recordings = await _unitOfWork.TranslationRoomArtifactRepository.GetRecordingsCreatedAsync(from, to, ct);

        return Result.Success(MediaUsageCalculator.Hourly(rooms, participants, recordings, from, to, _time.GetUtcNow().UtcDateTime));
    }
}
