using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.DTOs;

namespace WarpTalk.TranscriptService.Application.Interfaces;

public interface IGlossaryService
{
    /// <summary>
    /// WT-558: returns the glossary it created, so the caller can go on to put terms in it.
    ///
    /// It used to answer a bare 201 with no body, which meant a client that had just made a
    /// glossary had no id for it and had to re-list the workspace's glossaries and guess which
    /// one was new by name. Adding terms while creating — the thing the ticket asks for — is not
    /// buildable on a guess.
    /// </summary>
    Task<Result<GlossaryDto>> CreateGlossaryAsync(CreateGlossaryDto dto, CancellationToken cancellationToken = default);
    Task<Result<GlossaryDto>> GetGlossaryByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<GlossaryDto>>> GetGlossariesByWorkspaceIdAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<Result> UpdateGlossaryAsync(Guid id, UpdateGlossaryDto dto, CancellationToken cancellationToken = default);
    Task<Result> DeleteGlossaryAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result> AddTermAsync(Guid glossaryId, CreateGlossaryTermDto dto, CancellationToken cancellationToken = default);

    /// <summary>
    /// WT-472: import many terms in one transaction, skipping duplicates and reporting how many
    /// were skipped. See the implementation for why duplicates are skipped rather than rejected.
    /// </summary>
    Task<Result<BulkImportGlossaryTermsResultDto>> BulkImportTermsAsync(
        Guid glossaryId,
        BulkImportGlossaryTermsDto dto,
        CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<GlossaryTermDto>>> GetTermsByGlossaryIdAsync(Guid glossaryId, CancellationToken cancellationToken = default);
    Task<Result> UpdateTermAsync(Guid glossaryId, Guid termId, UpdateGlossaryTermDto dto, CancellationToken cancellationToken = default);
    Task<Result> DeleteTermAsync(Guid glossaryId, Guid termId, CancellationToken cancellationToken = default);
}
