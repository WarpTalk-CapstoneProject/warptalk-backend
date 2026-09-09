using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

/// <summary>
/// The document ids one caller may have the assistant answer from.
/// </summary>
/// <param name="DocumentIds">
/// Ids only. The caller is the AI pipeline, which needs them to scope a vector query — it has the
/// chunk text already and has no use for metadata it would then have to be trusted not to leak.
/// </param>
/// <param name="Truncated">
/// True when the workspace holds more retrievable documents than the cap. Reported rather than
/// swallowed: a silently short list reads to the person asking as "the assistant does not know
/// about that document", which is indistinguishable from a permission problem.
/// </param>
public record AiRetrievableDocumentsDto(IReadOnlyList<Guid> DocumentIds, bool Truncated);
