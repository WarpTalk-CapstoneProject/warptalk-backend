using System;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// The header that turns a presigned storage link into a saved file with the meeting's name on it.
///
/// A pure string builder rather than something only reachable through the S3 client, precisely so
/// the interesting part — what happens to "Họp sprint" on the way into an ASCII-only header field —
/// can be read back here instead of out of a signed URL.
/// </summary>
public class ContentDispositionHeaderTests
{
    [Fact]
    public void BothSpellingsOfTheNameAreSent()
    {
        // RFC 6266 §4.1: `filename` is ASCII-only, `filename*` (RFC 5987) carries the real name.
        // Neither alone is enough — one client would get no name, the other a corrected one.
        ContentDispositionHeader.Attachment("Họp sprint 42 - Recording - 2026-09-18.mp4")
            .Should().Be(
                "attachment; "
                + "filename=\"Hop sprint 42 - Recording - 2026-09-18.mp4\"; "
                + "filename*=UTF-8''H%E1%BB%8Dp%20sprint%2042%20-%20Recording%20-%202026-09-18.mp4");
    }

    [Fact]
    public void TheVietnameseNameSurvivesTheEncodingIntact()
    {
        // The property that actually matters: whatever we did to it, a client that decodes
        // `filename*` gets back the exact characters the host typed.
        const string name = "Đánh giá quý 3 - Transcript (VI) - 2026-09-18.txt";

        var encoded = ContentDispositionHeader.Rfc5987Encode(name);

        Uri.UnescapeDataString(encoded).Should().Be(name);
    }

    [Fact]
    public void TheAsciiFallbackIsATransliterationNotARowOfEscapes()
    {
        // This string is a last resort nobody normally sees, but when it is used it should still
        // read as the meeting's name.
        ContentDispositionHeader.AsciiFallback("Họp giao ban").Should().Be("Hop giao ban");
    }

    [Fact]
    public void TheVietnameseDIsMappedByHandBecauseUnicodeWillNotDecomposeIt()
    {
        // đ/Đ are single code points with no decomposition, so they sail through FormD and would
        // otherwise fall to the underscore: "Đánh giá" → "_anh gia".
        ContentDispositionHeader.AsciiFallback("Đánh giá đầu tư").Should().Be("Danh gia dau tu");
    }

    [Fact]
    public void ScriptTheFallbackCannotSpellBecomesUnderscoresRatherThanNothing()
    {
        // A name that vanishes entirely tells the reader nothing about which file they got.
        ContentDispositionHeader.AsciiFallback("会議 notes").Should().Be("__ notes");
    }

    [Fact]
    public void TheQuotedStringCannotBeBrokenOutOf()
    {
        // A quote or a backslash inside `filename="…"` would end or escape the field. Clean()
        // already refuses both as file-name characters; this is the direct-caller guard.
        var header = ContentDispositionHeader.Attachment("a\"b\\c.txt");

        ContentDispositionHeader.AsciiFallback("a\"b\\c.txt").Should().Be("a_b_c.txt");
        header.Should().StartWith("attachment; filename=\"");
        header.Should().NotContain("\"b");
    }

    [Fact]
    public void AnApostropheIsEncodedBecauseItIsThisParametersOwnDelimiter()
    {
        // Uri.EscapeDataString leaves ' alone, and `filename*=UTF-8''…` is split on apostrophes.
        ContentDispositionHeader.Rfc5987Encode("Bob's sync").Should().Be("Bob%27s%20sync");
    }

    [Fact]
    public void NoNameStillProducesAnAttachment()
    {
        // The file should still be saved rather than shown; we just have nothing to call it.
        ContentDispositionHeader.Attachment(null).Should().Be("attachment");
        ContentDispositionHeader.Attachment("   ").Should().Be("attachment");
    }

    [Fact]
    public void ANewlineCannotSmuggleASecondHeaderIn()
    {
        var header = ContentDispositionHeader.Attachment("sync\r\nX-Evil: 1.txt");

        header.Should().NotContain("\r");
        header.Should().NotContain("\n");
    }
}
