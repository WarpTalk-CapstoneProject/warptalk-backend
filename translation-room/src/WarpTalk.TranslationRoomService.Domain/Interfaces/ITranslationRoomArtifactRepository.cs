using WarpTalk.TranslationRoomService.Domain.Entities;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface ITranslationRoomArtifactRepository : IGenericRepository<TranslationRoomArtifact>
{
    Task<List<TranslationRoomArtifact>> GetArtifactsByRoomIdAsync(Guid roomId, CancellationToken ct = default);
    Task<TranslationRoomArtifact?> GetArtifactWithRoomAsync(Guid artifactId, CancellationToken ct = default);

    /// <summary>LiveKit egress recordings created in [from, to), with their room's workspace (admin Providers page).</summary>
    Task<IReadOnlyList<MediaUsageRecording>> GetRecordingsCreatedAsync(
        DateTime from,
        DateTime to,
        CancellationToken ct = default);
}

/// <summary>One egress recording: when it was created, whose it is, how big it ended up (null: not finished or not measured).</summary>
public sealed record MediaUsageRecording(Guid WorkspaceId, DateTime CreatedAt, long? SizeBytes);
