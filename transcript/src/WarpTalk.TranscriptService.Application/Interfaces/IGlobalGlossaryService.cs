using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.DTOs;

namespace WarpTalk.TranscriptService.Application.Interfaces;

public interface IGlobalGlossaryService
{
    Task<Result<PagedResultDto<GlobalGlossaryTermDto>>> GetTermsAsync(GlobalGlossaryTermQuery query, CancellationToken cancellationToken = default);
    Task<Result<GlobalGlossaryTermDto>> GetTermByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<GlobalGlossaryTermDto>> CreateTermAsync(CreateGlobalGlossaryTermDto dto, Guid actorUserId, CancellationToken cancellationToken = default);
    Task<Result> UpdateTermAsync(Guid id, UpdateGlobalGlossaryTermDto dto, Guid actorUserId, CancellationToken cancellationToken = default);
    Task<Result> DeleteTermAsync(Guid id, Guid actorUserId, CancellationToken cancellationToken = default);
    Task<Result> PublishTermAsync(Guid id, Guid actorUserId, CancellationToken cancellationToken = default);
    Task<Result> ArchiveTermAsync(Guid id, Guid actorUserId, CancellationToken cancellationToken = default);
    Task<Result<BulkImportResultDto>> BulkImportAsync(BulkImportGlobalGlossaryTermsDto dto, Guid actorUserId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<GlobalGlossaryAuditDto>>> GetAuditsAsync(Guid termId, CancellationToken cancellationToken = default);
}
