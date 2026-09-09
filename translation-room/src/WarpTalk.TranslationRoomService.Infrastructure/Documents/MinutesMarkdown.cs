using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WarpTalk.TranslationRoomService.Infrastructure.Documents;

/// <summary>
/// The Markdown the narrative arrives as, read into blocks a Word writer can set.
///
/// WHY THIS EXISTS AT ALL
///     warptalk-ai writes the overview as a Markdown document — "## Meeting Summary", "- ",
///     "**80%**". Word has no idea what any of that is, so a writer that prints the string
///     verbatim puts hash marks and asterisks into a document somebody signs. That is not a
///     cosmetic complaint: a reader who sees "**80% correct**" on a signed page cannot tell
///     whether the asterisks are the model's formatting or part of the figure.
///
/// WHY IT IS SHARED AND THE LAYOUTS ARE NOT
///     <see cref="MeetingMinutesDocxWriter"/> and <see cref="GlobalMinutesDocxWriter"/> stay
///     separate on purpose — they are different forms, not one form with switches. But reading
///     "- " as a list item is not layout, it is text handling, and two copies of it would be two
///     places for the same document to be mis-read. So the parse is here and the SETTING stays
///     with each writer: the Vietnamese form indents its bullets one way, the international form
///     another, and both start from the same blocks.
///
/// WHAT IT DOES NOT DO
///     It is not a Markdown implementation. Tables, block quotes, images, reference links and
///     setext headings are left as the text they are, because a meeting summary does not contain
///     them and a half-supported feature is worse than an unsupported one. Nothing is ever
///     DROPPED: markup this class does not recognise stays on the page as the characters the
///     model wrote.
/// </summary>
internal static class MinutesMarkdown
{
    public enum BlockKind
    {
        /// <summary>A run of prose.</summary>
        Paragraph,

        /// <summary>"## Something" — a sub-heading inside a section the writer already titled.</summary>
        Heading,

        /// <summary>A list item, bulleted or numbered.</summary>
        Item
    }

    /// <param name="Depth">Nesting level for an <see cref="BlockKind.Item"/>, 0 at the top.</param>
    /// <param name="Marker">
    /// The list marker to print: null for a bullet the writer chooses the glyph for, or the
    /// original "3." when the model numbered the item itself. Kept verbatim because a numbered
    /// list renumbered by a renderer stops matching the sentence above that referred to item 3.
    /// </param>
    public readonly record struct Block(BlockKind Kind, string Text, int Depth, string? Marker);

    public readonly record struct Span(string Text, bool Bold, bool Italic);

    /// <summary>Inline: **bold**, __bold__, *italic*, _italic_, `code`, [text](url).</summary>
    private static readonly Regex InlineMarkup = new(
        @"\*\*(?<b>[^*]+)\*\*|__(?<b>[^_]+)__|\*(?<i>[^*]+)\*|(?<!\w)_(?<i>[^_]+)_(?!\w)"
        + @"|`(?<c>[^`]+)`|\[(?<t>[^\]]+)\]\((?<u>[^)\s]+)\)",
        RegexOptions.Compiled);

    private static readonly Regex HeadingLine = new(@"^(?<hashes>#{1,6})\s+(?<text>.+)$", RegexOptions.Compiled);

    private static readonly Regex BulletLine = new(@"^(?<lead>[ \t]*)[-*•+]\s+(?<text>.+)$", RegexOptions.Compiled);

    private static readonly Regex NumberedLine = new(
        @"^(?<lead>[ \t]*)(?<marker>\d{1,2}[.)])\s+(?<text>.+)$", RegexOptions.Compiled);

    private static readonly Regex RuleLine = new(@"^\s*([-*_])\1{2,}\s*$", RegexOptions.Compiled);

    /// <summary>
    /// The text as blocks, in the order it was written.
    ///
    /// ONE LINE IS ONE BLOCK, deliberately. Markdown proper joins consecutive prose lines into a
    /// paragraph, and doing that here would be correct for hand-wrapped text and wrong for what
    /// this actually receives: a model writing one fact per line. Joining would run two facts into
    /// one sentence, which is the more expensive mistake of the two.
    /// </summary>
    public static IReadOnlyList<Block> Parse(string? text)
    {
        var blocks = new List<Block>();
        if (string.IsNullOrWhiteSpace(text)) return blocks;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();

            // Blank lines and horizontal rules are spacing in Markdown, and a Word document
            // carries its spacing in the paragraph properties instead.
            if (string.IsNullOrWhiteSpace(line) || RuleLine.IsMatch(line)) continue;

            var heading = HeadingLine.Match(line);
            if (heading.Success)
            {
                blocks.Add(new Block(
                    BlockKind.Heading,
                    heading.Groups["text"].Value.Trim(),
                    heading.Groups["hashes"].Value.Length,
                    null));
                continue;
            }

            var bullet = BulletLine.Match(line);
            if (bullet.Success)
            {
                blocks.Add(new Block(
                    BlockKind.Item,
                    bullet.Groups["text"].Value.Trim(),
                    Depth(bullet.Groups["lead"].Value),
                    null));
                continue;
            }

            var numbered = NumberedLine.Match(line);
            if (numbered.Success)
            {
                blocks.Add(new Block(
                    BlockKind.Item,
                    numbered.Groups["text"].Value.Trim(),
                    Depth(numbered.Groups["lead"].Value),
                    numbered.Groups["marker"].Value));
                continue;
            }

            blocks.Add(new Block(BlockKind.Paragraph, line.Trim(), 0, null));
        }

        return blocks;
    }

    /// <summary>How deep a nested list item sits: two spaces, or one tab, per level.</summary>
    private static int Depth(string lead)
    {
        var spaces = lead.Sum(character => character == '\t' ? 2 : 1);
        return Math.Min(spaces / 2, 3);
    }

    /// <summary>
    /// One line split into runs, with the emphasis markers turned into emphasis and removed from
    /// the text. A line with no markup comes back as a single span, so a caller can always render
    /// the result the same way.
    /// </summary>
    public static IReadOnlyList<Span> InlineSpans(string? text)
    {
        var value = text ?? string.Empty;
        var spans = new List<Span>();
        var position = 0;

        foreach (Match match in InlineMarkup.Matches(value))
        {
            if (match.Index > position)
            {
                spans.Add(new Span(value[position..match.Index], false, false));
            }

            if (match.Groups["b"].Success) spans.Add(new Span(match.Groups["b"].Value, true, false));
            else if (match.Groups["i"].Success) spans.Add(new Span(match.Groups["i"].Value, false, true));
            else if (match.Groups["c"].Success) spans.Add(new Span(match.Groups["c"].Value, false, false));
            // A link keeps its target: dropping the URL from a record loses the only thing that
            // makes the reference checkable.
            else if (match.Groups["t"].Success)
            {
                spans.Add(new Span($"{match.Groups["t"].Value} ({match.Groups["u"].Value})", false, false));
            }

            position = match.Index + match.Length;
        }

        if (position < value.Length) spans.Add(new Span(value[position..], false, false));
        if (spans.Count == 0) spans.Add(new Span(value, false, false));

        return spans;
    }
}
