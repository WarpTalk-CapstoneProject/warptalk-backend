namespace WarpTalk.WorkspaceService.Application.Evaluators;

/// <summary>Which version of a document's content a caller is given.</summary>
public enum DocumentContentVersion
{
    /// <summary>Nothing. No bytes, no text.</summary>
    None,

    /// <summary>The file as uploaded.</summary>
    Original,

    /// <summary>The PII-masked copy: same format, personal details replaced by markers.</summary>
    Masked
}

/// <summary>
/// Everything the content decision depends on, already looked up. No I/O happens past this point.
/// </summary>
/// <param name="IsOwnerOrAdmin">The caller's workspace role is Owner or Admin.</param>
/// <param name="IsUploader">The caller uploaded the document or owns it.</param>
/// <param name="IsRestricted">The document's confidentiality level is `restricted`.</param>
/// <param name="AccessGranted">
/// What <see cref="IDocumentAccessEvaluator"/> answers for the permission being asked, on the
/// document as it is.
/// </param>
/// <param name="AccessGrantedIgnoringRestriction">
/// The same question with the restricted gate taken out: would this caller be let in if the
/// document were not restricted? Every other rule — explicit DENY, private, pending approval,
/// archived, External membership — still applies.
/// </param>
/// <param name="MaskedVersionExists">A masked copy of the current file is in storage.</param>
/// <param name="RestrictedByPii">
/// The last scan restricted the document for personal details and not for a banned keyword.
/// </param>
public readonly record struct DocumentContentAccessInput(
    bool IsOwnerOrAdmin,
    bool IsUploader,
    bool IsRestricted,
    bool AccessGranted,
    bool AccessGrantedIgnoringRestriction,
    bool MaskedVersionExists,
    bool RestrictedByPii);

/// <summary>
/// THE one rule for which version of a document somebody gets: original, masked, or nothing.
/// </summary>
/// <remarks>
/// Used by the detail route (what the page is told to show), by the original download and
/// extracted-text routes (which refuse anyone not entitled to <see cref="DocumentContentVersion.Original"/>),
/// and by the masked download. One function, so those cannot drift apart: a page that offers a
/// version the download then refuses — or worse, the reverse — is not possible to write.
///
/// The decisions, as the product owner made them (2026-10-02):
///
///   • A document that is NOT restricted behaves exactly as it always has.
///   • Owner/Admin and the uploader get the original of a restricted document, as before.
///   • Anyone else gets the MASKED copy of a restricted document — and only when one exists.
///     That includes a member holding an ALLOW policy: an "original" grant is a later wave.
///   • A document restricted for personal details with no masked copy (not produced yet, could
///     not be produced, file unreadable) gives a member NOTHING. Never the original.
///   • A document restricted for another reason — a banned keyword, a failed scan, a label
///     somebody set by hand — has no masked copy to show and stays exactly as it was: closed to
///     members, open to a member an Owner/Admin explicitly allowed.
///
/// It is pure on purpose. Every input is a fact somebody else established; this only combines
/// them, which is what lets the tests walk the whole table.
/// </remarks>
public static class DocumentContentAccessDecision
{
    public static DocumentContentVersion Decide(in DocumentContentAccessInput input)
    {
        if (!input.IsRestricted)
        {
            return input.AccessGranted ? DocumentContentVersion.Original : DocumentContentVersion.None;
        }

        if (input.IsOwnerOrAdmin || input.IsUploader)
        {
            // Still subject to the evaluator: an explicit DENY aimed at the uploader holds.
            return input.AccessGranted ? DocumentContentVersion.Original : DocumentContentVersion.None;
        }

        if (input.MaskedVersionExists)
        {
            return input.AccessGranted || input.AccessGrantedIgnoringRestriction
                ? DocumentContentVersion.Masked
                : DocumentContentVersion.None;
        }

        if (input.RestrictedByPii)
        {
            // Fail closed. The copy that would make this readable is not there.
            return DocumentContentVersion.None;
        }

        return input.AccessGranted ? DocumentContentVersion.Original : DocumentContentVersion.None;
    }

    /// <summary>
    /// May the caller open the document's page at all — its name and metadata — even when
    /// <see cref="Decide"/> gives them no content?
    /// </summary>
    /// <remarks>
    /// The one case beyond "has content": a member looking at a document restricted for personal
    /// details whose masked copy is not there yet. They are shown an honest empty state rather
    /// than a 403, because a masked copy is something this document is expected to have.
    /// A document restricted for any other reason stays a 403, as today.
    /// </remarks>
    public static bool CanOpenDocument(in DocumentContentAccessInput input)
    {
        if (Decide(input) != DocumentContentVersion.None)
        {
            return true;
        }

        return input.IsRestricted
            && !input.IsOwnerOrAdmin
            && !input.IsUploader
            && input.RestrictedByPii
            && input.AccessGrantedIgnoringRestriction;
    }

    /// <summary>
    /// May the caller read the masked copy? Anyone whose content is the masked copy, and anyone
    /// entitled to the original who wants to see what members see.
    /// </summary>
    public static bool CanReadMaskedVersion(in DocumentContentAccessInput input) =>
        input.IsRestricted
        && input.MaskedVersionExists
        && Decide(input) != DocumentContentVersion.None;

    public static string ToWireValue(this DocumentContentVersion version) => version switch
    {
        DocumentContentVersion.Original => "original",
        DocumentContentVersion.Masked => "masked",
        _ => "none"
    };
}
