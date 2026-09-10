using System;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

/// <summary>
/// One reader's (shape, language) rendering of a meeting's summary, kept beside the canonical
/// one rather than on top of it.
///
/// The room's summary lives in a single <see cref="TranslationRoomArtifact"/> of type
/// SUMMARY_EXPORT, and rewriting it REPLACES that row. Since the rewrite gate admits every
/// participant, a Japanese attendee asking to read the meeting in Japanese used to destroy the
/// English summary the host had published — for everyone, until the host pressed the button
/// again. These rows are the other half of the fix: the artifact keeps meaning "what the host
/// published", and everything else a reader asks for lands here.
///
/// EVERY ROW IS DISPOSABLE. Nothing is authored or signed; each one can be rebuilt by asking
/// the model again from the same stored transcript. That is what makes it correct to key them
/// uniquely and overwrite in place: this exists so the SECOND reader of a language does not pay
/// for it again, not to accumulate history.
/// </summary>
public partial class TranslationRoomSummaryVariant
{
    public Guid Id { get; set; }

    public Guid TranslationRoomId { get; set; }

    /// <summary>
    /// A key <c>summary_templates.resolve_template</c> knows: general, standup, interview, demo,
    /// technical, traceable. Free text rather than an enum on purpose — the template set is data
    /// on the AI side, and adding one there must not need a migration here.
    /// </summary>
    public string TemplateKey { get; set; } = null!;

    /// <summary>
    /// Bare ISO 639-1, normalised before it ever reaches this type ("vi-VN" arrives as "vi").
    /// Empty string — never null — means the summary follows the transcript. "As spoken" is a
    /// real answer, and a null here would let the same variant be inserted without limit,
    /// because Postgres does not consider two NULLs equal in a unique index.
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>The same JSON shape the canonical summary artifact stores, so one parser reads both.</summary>
    public string Content { get; set; } = null!;

    /// <summary>
    /// Who asked for this rendering first. Support answers a question with it; access never
    /// does — a variant is readable by anyone who may read the room's artifacts, exactly like
    /// the canonical summary beside it, and is not private to whoever generated it.
    /// </summary>
    public Guid? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Moved on every regeneration. This is what lets staleness be answered for a variant the
    /// same way it already is for the canonical artifact — the web compares it against the
    /// transcript's segments — instead of a pre-correction rendering reading as current forever.
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    public virtual TranslationRoom TranslationRoom { get; set; } = null!;
}
