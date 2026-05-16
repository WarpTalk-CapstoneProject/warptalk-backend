using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.DTOs;

namespace WarpTalk.TranscriptService.Application.Interfaces;

public interface IGlossaryService
{
    Task<Result> CreateGlossaryAsync(CreateGlossaryDto dto, CancellationToken cancellationToken = default);
    Task<Result<GlossaryDto>> GetGlossaryByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<GlossaryDto>>> GetGlossariesByWorkspaceIdAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<Result> UpdateGlossaryAsync(Guid id, UpdateGlossaryDto dto, CancellationToken cancellationToken = default);
    Task<Result> DeleteGlossaryAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result> AddTermAsync(Guid glossaryId, CreateGlossaryTermDto dto, CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<GlossaryTermDto>>> GetTermsByGlossaryIdAsync(Guid glossaryId, CancellationToken cancellationToken = default);
    Task<Result> UpdateTermAsync(Guid glossaryId, Guid termId, UpdateGlossaryTermDto dto, CancellationToken cancellationToken = default);
    Task<Result> DeleteTermAsync(Guid glossaryId, Guid termId, CancellationToken cancellationToken = default);
}
