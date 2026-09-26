using System;
using System.Globalization;
using System.Text;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// The <c>Content-Disposition</c> value that turns a signed storage link into a file the browser
/// saves, under the name we chose.
///
/// WHY THE HEADER AND NOT THE URL
///     The recording lives in object storage and is served by R2, not by us — the browser is sent
///     straight there with a presigned link. Nothing in that URL is ours to name: the object key is
///     "rooms/&lt;guid&gt;/&lt;egress-id&gt;.mp4" and that is what a browser saves it as. The only
///     way to say "call it Họp sprint 42 - Recording - 2026-09-18.mp4" is to ask the signer to add
///     a response-header override to the signature, which is what this string is for.
///
/// WHY TWO SPELLINGS OF THE SAME NAME
///     RFC 6266 §4.1 with RFC 5987 §3.2: <c>filename</c> is limited to a quoted ASCII token, and
///     <c>filename*</c> carries the real one as percent-encoded UTF-8. Both are sent because
///     neither alone is enough — a client that only understands <c>filename</c> would otherwise
///     get no name at all, and a name restricted to what <c>filename</c> can hold would spell
///     "Họp sprint" as something the host never typed. Every browser in use prefers
///     <c>filename*</c> when it is there, so the ASCII line is a fallback nobody normally sees.
///
/// THE ASCII FALLBACK IS A TRANSLITERATION, NOT A TRUNCATION
///     "Họp sprint" becomes "Hop sprint", not "H p sprint" and not "%E1%BB%8Dp". The accents are
///     dropped by decomposing each letter and discarding the combining marks; đ and Đ are mapped
///     by hand because Unicode does not decompose them. This string is only ever a last resort, but
///     when it is used it should still read as the meeting's name.
/// </summary>
public static class ContentDispositionHeader
{
    /// <summary>
    /// <c>attachment; filename="…"; filename*=UTF-8''…</c> for the given name, or a bare
    /// <c>attachment</c> when there is no usable name to offer.
    /// </summary>
    public static string Attachment(string? fileName)
    {
        var name = DocumentFileName.Clean(fileName);
        if (name.Length == 0) return "attachment";

        return $"attachment; filename=\"{AsciiFallback(name)}\"; filename*=UTF-8''{Rfc5987Encode(name)}";
    }

    /// <summary>
    /// The name with its accents folded away and anything still non-ASCII replaced by an
    /// underscore, so it can sit inside a quoted-string without escaping and without lying.
    /// </summary>
    public static string AsciiFallback(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            // The accents themselves, left behind by the decomposition above.
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            switch (character)
            {
                // Vietnamese đ/Đ are single code points with no decomposition, so they survive
                // FormD intact and would otherwise fall to the underscore below — turning
                // "Đánh giá" into "_anh gia".
                case 'đ': builder.Append('d'); continue;
                case 'Đ': builder.Append('D'); continue;
            }

            if (character < 32 || character == 127)
            {
                // A control character inside a quoted-string is not just unreadable, it can end
                // the header early. Dropped outright.
                continue;
            }

            // A quote or a backslash would close or escape the quoted-string. They cannot reach
            // here through DocumentFileName.Clean (both are refused file-name characters), but
            // this method is also the one a future caller will reach for directly.
            if (character is '"' or '\\')
            {
                builder.Append('_');
                continue;
            }

            builder.Append(character > 127 ? '_' : character);
        }

        var ascii = builder.ToString().Trim();
        return ascii.Length == 0 ? "download" : ascii;
    }

    /// <summary>
    /// RFC 5987 §3.2 <c>value-chars</c>: the unreserved <c>attr-char</c> set verbatim, everything
    /// else as percent-encoded UTF-8 bytes. Not <see cref="Uri.EscapeDataString"/>, which leaves
    /// <c>!</c> and <c>~</c> alone but also leaves <c>'</c> — and an apostrophe is the delimiter
    /// this very parameter is split on.
    /// </summary>
    public static string Rfc5987Encode(string value)
    {
        var builder = new StringBuilder(value.Length * 2);

        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var character = (char)b;
            var isAttributeChar =
                char.IsAsciiLetterOrDigit(character) ||
                character is '!' or '#' or '$' or '&' or '+' or '-' or '.'
                    or '^' or '_' or '`' or '|' or '~';

            if (isAttributeChar) builder.Append(character);
            else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
