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
}
