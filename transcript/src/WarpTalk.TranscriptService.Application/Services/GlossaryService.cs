using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Application.Mappers;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;

namespace WarpTalk.TranscriptService.Application.Services;

public class GlossaryService : IGlossaryService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<GlossaryService> _logger;

    public GlossaryService(IUnitOfWork unitOfWork, ILogger<GlossaryService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result> CreateGlossaryAsync(CreateGlossaryDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            var glossary = dto.ToEntity();

            await _unitOfWork.Glossaries.AddAsync(glossary, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating glossary for workspace {WorkspaceId}", dto.WorkspaceId);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<GlossaryDto>> GetGlossaryByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var glossary = await _unitOfWork.Glossaries.GetByIdAsync(id, cancellationToken);
            if (glossary == null)
                return Result.Failure<GlossaryDto>($"Glossary with ID {id} not found.", "NOT_FOUND");

            return Result.Success(glossary.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting glossary {GlossaryId}", id);
            return Result.Failure<GlossaryDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<IEnumerable<GlossaryDto>>> GetGlossariesByWorkspaceIdAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var glossaries = await _unitOfWork.Glossaries.FindAsync(g => g.WorkspaceId == workspaceId, cancellationToken);
            return Result.Success(glossaries.Select(g => g.ToDto()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting glossaries for workspace {WorkspaceId}", workspaceId);
            return Result.Failure<IEnumerable<GlossaryDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> UpdateGlossaryAsync(Guid id, UpdateGlossaryDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            var glossary = await _unitOfWork.Glossaries.GetByIdAsync(id, cancellationToken);
            if (glossary == null)
                return Result.Failure<GlossaryDto>($"Glossary with ID {id} not found.", "NOT_FOUND");

            glossary.Name = dto.Name;
            glossary.Description = dto.Description;
            glossary.IsActive = dto.IsActive;
            glossary.UpdatedAt = DateTime.UtcNow;

            _unitOfWork.Glossaries.Update(glossary);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating glossary {GlossaryId}", id);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> DeleteGlossaryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var glossary = await _unitOfWork.Glossaries.GetByIdAsync(id, cancellationToken);
            if (glossary == null)
                return Result.Failure($"Glossary with ID {id} not found.", "NOT_FOUND");

            _unitOfWork.Glossaries.Remove(glossary);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting glossary {GlossaryId}", id);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> AddTermAsync(Guid glossaryId, CreateGlossaryTermDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            var glossary = await _unitOfWork.Glossaries.GetByIdAsync(glossaryId, cancellationToken);
            if (glossary == null)
                return Result.Failure($"Glossary with ID {glossaryId} not found.", "NOT_FOUND");

            var term = dto.ToEntity(glossaryId);

            await _unitOfWork.GlossaryTerms.AddAsync(term, cancellationToken);
            
            glossary.TermCount++;
            glossary.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.Glossaries.Update(glossary);

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding term to glossary {GlossaryId}", glossaryId);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<IEnumerable<GlossaryTermDto>>> GetTermsByGlossaryIdAsync(Guid glossaryId, CancellationToken cancellationToken = default)
    {
        try
        {
            var terms = await _unitOfWork.GlossaryTerms.FindAsync(t => t.GlossaryId == glossaryId, cancellationToken);
            return Result.Success(terms.Select(t => t.ToDto()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting terms for glossary {GlossaryId}", glossaryId);
            return Result.Failure<IEnumerable<GlossaryTermDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> UpdateTermAsync(Guid glossaryId, Guid termId, UpdateGlossaryTermDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            var term = await _unitOfWork.GlossaryTerms.GetByIdAsync(termId, cancellationToken);
            if (term == null)
                return Result.Failure($"Term with ID {termId} not found.", "NOT_FOUND");

            if (term.GlossaryId != glossaryId)
                return Result.Failure("Term does not belong to the specified Glossary.", "BAD_REQUEST");

            term.SourceTerm = dto.SourceTerm;
            term.TargetTerm = dto.TargetTerm;
            term.Context = dto.Context;
            term.Domain = dto.Domain;
            term.Priority = dto.Priority;
            term.IsActive = dto.IsActive;
            term.UpdatedAt = DateTime.UtcNow;

            _unitOfWork.GlossaryTerms.Update(term);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating term {TermId}", termId);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> DeleteTermAsync(Guid glossaryId, Guid termId, CancellationToken cancellationToken = default)
    {
        try
        {
            var term = await _unitOfWork.GlossaryTerms.GetByIdAsync(termId, cancellationToken);
            if (term == null)
                return Result.Failure($"Term with ID {termId} not found.", "NOT_FOUND");

            if (term.GlossaryId != glossaryId)
                return Result.Failure("Term does not belong to the specified Glossary.", "BAD_REQUEST");

            _unitOfWork.GlossaryTerms.Remove(term);

            var glossary = await _unitOfWork.Glossaries.GetByIdAsync(glossaryId, cancellationToken);
            if (glossary != null)
            {
                glossary.TermCount = Math.Max(0, glossary.TermCount - 1);
                glossary.UpdatedAt = DateTime.UtcNow;
                _unitOfWork.Glossaries.Update(glossary);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting term {TermId}", termId);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }
}
