using System;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// A meeting's summary in the shape and language THIS reader asked for.
///
/// <c>Status</c> is the whole point of the type. Generating a rendering takes an LLM call and a
/// round trip through the AI worker, so the first person to ask for a language gets
/// <c>generating</c> and the answer arrives later; everybody after them gets <c>ready</c> and the
/// content immediately. Returning a 404 for the first case would tell the client the summary does
/// not exist, which is a different and wrong thing.
/// </summary>
/// <param name="TemplateKey">Echoed back so a client that changed its mind mid-flight can tell whose answer this is.</param>
/// <param name="Language">Bare ISO 639-1, or empty for "as spoken".</param>
/// <param name="Content">The summary JSON, or null while it is still being written.</param>
/// <param name="IsCanonical">
/// True when this is the room's published summary rather than a rendering beside it. The client
/// uses it to say "this is the summary the host published" instead of implying every language is
/// equally official — and to know that regenerating THIS one is what changes what others see.
/// </param>
/// <param name="UpdatedAt">
/// When this rendering was last written. Null while generating. The staleness check compares it
/// against the transcript's own segments, which is why a variant has to carry its own timestamp
/// rather than borrowing the canonical artifact's.
/// </param>
/// <param name="Error">
/// Why this rendering is not coming, in the words the worker wrote. Set only alongside
/// <see cref="SummaryVariantStatus.Failed"/>. Before it existed a rendering that failed could
/// only keep answering "generating" until the client's own deadline turned it into a sentence
/// that named nothing.
/// </param>
public record SummaryVariantDto(
    string TemplateKey,
    string Language,
    string? Content,
    bool IsCanonical,
    string Status,
    DateTime? UpdatedAt,
    string? Error = null);

/// <summary>The renderings a room already holds, so a picker can say which choices are instant.</summary>
/// <param name="TemplateKey">The shape this rendering is in.</param>
/// <param name="Language">Bare ISO 639-1, or empty for "as spoken".</param>
/// <param name="IsCanonical">Whether this is the published summary rather than a rendering beside it.</param>
/// <param name="UpdatedAt">When it was last written.</param>
public record SummaryVariantSummaryDto(
    string TemplateKey,
    string Language,
    bool IsCanonical,
    DateTime UpdatedAt);

public static class SummaryVariantStatus
{
    /// <summary>The content is in this response.</summary>
    public const string Ready = "ready";

    /// <summary>Queued with the AI worker. The client refetches; nothing is wrong.</summary>
    public const string Generating = "generating";

    /// <summary>
    /// It is not coming, and <see cref="SummaryVariantDto.Error"/> says why.
    ///
    /// This status did not exist, and its absence was itself the bug: a rendering that failed had
    /// no way to be anything but "generating", so the only end a reader ever saw was their own
    /// client giving up after ninety seconds and saying something had not arrived.
    /// </summary>
    public const string Failed = "failed";
}
