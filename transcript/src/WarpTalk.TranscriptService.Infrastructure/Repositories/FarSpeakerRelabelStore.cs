using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranscriptService.Application.FarSpeakers;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

namespace WarpTalk.TranscriptService.Infrastructure.Repositories;

/// <inheritdoc cref="IFarSpeakerRelabelStore"/>
public sealed class FarSpeakerRelabelStore : IFarSpeakerRelabelStore
{
    private readonly TranscriptDbContext _context;

    public FarSpeakerRelabelStore(TranscriptDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Guid>> FindUnqueuedBridgeRoomsAsync(
        Guid standInId,
        DateTime since,
        int limit,
        CancellationToken ct = default)
    {
        // Driven by transcript_segments_speaker_participant_id_idx: the stand-in's rows are a small
        // slice of the table, and only recent ones are looked at.
        return await _context.TranscriptSegments
            .AsNoTracking()
            .Where(s => s.SpeakerParticipantId == standInId && s.CreatedAt >= since)
            .Select(s => s.Transcript.TranslationRoomId)
            .Distinct()
            .Where(roomId => !_context.FarSpeakerRelabelJobs.Any(j => j.TranslationRoomId == roomId))
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task AddJobsAsync(IEnumerable<FarSpeakerRelabelJob> jobs, CancellationToken ct = default)
    {
        await _context.FarSpeakerRelabelJobs.AddRangeAsync(jobs, ct);
    }

    public async Task<IReadOnlyList<FarSpeakerRelabelJob>> GetDueJobsAsync(DateTime now, int limit, CancellationToken ct = default)
    {
        return await _context.FarSpeakerRelabelJobs
            .Where(j => j.Status == FarSpeakerRelabelJobStatuses.Pending && j.NextAttemptAt <= now)
            .OrderBy(j => j.NextAttemptAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<StandInTranscript?> GetStandInTranscriptAsync(Guid roomId, Guid standInId, CancellationToken ct = default)
    {
        var transcript = await _context.Transcripts
            .AsNoTracking()
            .Where(t => t.TranslationRoomId == roomId && t.IsCurrent && t.DeletedAt == null)
            .Select(t => new { t.Id, t.TimelineAnchorAt })
            .FirstOrDefaultAsync(ct);
        if (transcript is null) return null;

        var segments = await _context.TranscriptSegments
            .Where(s => s.TranscriptId == transcript.Id && s.SpeakerParticipantId == standInId)
            .OrderBy(s => s.SequenceOrder)
            .ToListAsync(ct);

        return new StandInTranscript(transcript.Id, transcript.TimelineAnchorAt, segments);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);
}
