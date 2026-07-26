using System;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.Application.Mappers;

public static class GlobalGlossaryMapper
{
    public static GlobalGlossaryTermDto ToDto(this GlobalGlossaryTerm term)
    {
        return new GlobalGlossaryTermDto(
            term.Id,
            term.Term,
            term.PreferredTranslation,
            term.SourceLanguage,
            term.TargetLanguage,
            term.BusinessDomain,
            term.Definition,
            term.UsageNote,
            term.Priority,
            term.Status,
            term.Version,
            term.CreatedAt,
            term.CreatedBy,
            term.UpdatedAt,
            term.UpdatedBy
        );
    }

    public static GlobalGlossaryTerm ToEntity(this CreateGlobalGlossaryTermDto dto, Guid actorUserId)
    {
        return new GlobalGlossaryTerm
        {
            Id = Guid.NewGuid(),
            Term = dto.Term,
            PreferredTranslation = dto.PreferredTranslation,
            SourceLanguage = dto.SourceLanguage,
            TargetLanguage = dto.TargetLanguage,
            BusinessDomain = dto.BusinessDomain,
            Definition = dto.Definition,
            UsageNote = dto.UsageNote,
            Priority = dto.Priority,
            Status = "draft",
            Version = 1,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = actorUserId,
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = actorUserId,
        };
    }

    public static GlobalGlossaryAuditDto ToDto(this GlobalGlossaryAudit audit)
    {
        return new GlobalGlossaryAuditDto(
            audit.Id,
            audit.TermId,
            audit.Action,
            audit.BeforeJson,
            audit.AfterJson,
            audit.ActorUserId,
            audit.CreatedAt
        );
    }
}
