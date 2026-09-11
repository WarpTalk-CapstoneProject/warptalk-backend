namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

/// <summary>
/// What an upload actually did, so the controller can choose the status code and the web can
/// choose the sentence. WT-666.
/// </summary>
/// <remarks>
/// WHY THE SERVICE NO LONGER RETURNS THE DOCUMENT DIRECTLY.
///
/// A duplicate collision is not a success and not an error — it is a question, and the answer
/// needs the existing document attached to it. <c>Result</c> carries a message and a code on
/// failure and nothing else, so a 409 built from it could say "this file is already here" without
/// being able to say WHICH file, which makes the three choices WT-666 asks for unanswerable.
///
/// The HTTP shape of the success path is unchanged: the controller unwraps <see cref="Document"/>
/// and returns it as the body, exactly as before. Only the conflict is new.
/// </remarks>
/// <param name="Outcome">One of <see cref="Created"/>, <see cref="Skipped"/>, <see cref="Replaced"/>, <see cref="Duplicate"/>.</param>
/// <param name="Document">The stored document. Null only when <see cref="Outcome"/> is <see cref="Duplicate"/>.</param>
/// <param name="ExistingDocument">
/// The document already holding these bytes. Set when <see cref="Outcome"/> is
/// <see cref="Duplicate"/> AND the caller may view it — null when they may not, because a
/// collision must not become a way to learn that a document one cannot open exists.
/// </param>
public record UploadDocumentOutcomeDto(
    string Outcome,
    WorkspaceDocumentDto? Document,
    DocumentDuplicateDto? ExistingDocument = null)
{
    /// <summary>A new document row was written.</summary>
    public const string Created = "created";

    /// <summary>The bytes were already here; the caller asked to keep the existing document.</summary>
    public const string Skipped = "skipped";

    /// <summary>The caller replaced an existing document's file rather than adding a second copy.</summary>
    public const string Replaced = "replaced";

    /// <summary>The bytes were already here and the caller has not yet said what to do about it.</summary>
    public const string Duplicate = "duplicate";
}

/// <summary>
/// The 409 body for a duplicate upload: the usual error envelope plus the document it collided
/// with.
/// </summary>
public record DocumentDuplicateConflictResponse(
    string? Error,
    string? Code,
    DocumentDuplicateDto? Duplicate);
