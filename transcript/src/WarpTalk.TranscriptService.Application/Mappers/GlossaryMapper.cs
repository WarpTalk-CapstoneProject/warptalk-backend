using System;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.Application.Mappers;

public static class GlossaryMapper
{
    public static GlossaryDto ToDto(this Glossary glossary)
    {
        return new GlossaryDto(
            glossary.Id,
            glossary.WorkspaceId,
            glossary.Name,
            glossary.Description,
            glossary.SourceLanguage,
            glossary.TargetLanguage,
            glossary.TermCount,
            glossary.IsActive,
            glossary.CreatedAt,
            glossary.UpdatedAt
        );
    }

    public static Glossary ToEntity(this CreateGlossaryDto dto)
    {
        return new Glossary
        {
            Id = Guid.NewGuid(),
            WorkspaceId = dto.WorkspaceId,
            Name = dto.Name,
            Description = dto.Description,
            SourceLanguage = dto.SourceLanguage,
            TargetLanguage = dto.TargetLanguage,
            TermCount = 0,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public static GlossaryTermDto ToDto(this GlossaryTerm term)
    {
        return new GlossaryTermDto(
            term.Id,
            term.GlossaryId,
            term.SourceTerm,
            term.TargetTerm,
            term.Context,
            term.Domain,
            term.Priority,
            term.IsActive,
            term.CreatedAt,
            term.UpdatedAt
        );
    }

    public static GlossaryTerm ToEntity(this CreateGlossaryTermDto dto, Guid glossaryId)
    {
        return new GlossaryTerm
        {
            Id = Guid.NewGuid(),
            GlossaryId = glossaryId,
            SourceTerm = dto.SourceTerm,
            TargetTerm = dto.TargetTerm,
            Context = dto.Context,
            Domain = dto.Domain,
            Priority = dto.Priority,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }
}
