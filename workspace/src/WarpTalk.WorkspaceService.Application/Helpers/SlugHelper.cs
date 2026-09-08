using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Helpers;

public static class SlugHelper
{
    /// <summary>
    /// A workspace name turned into a URL segment, bounded by the column that has to hold it.
    ///
    /// The bound is the point. A slug is derived from a name that may be up to 150 characters, and
    /// workspaces.slug is varchar(100) — so the slug overflows BEFORE the name does, and nothing
    /// used to stand between the two. A name of 101 ordinary characters was accepted, slugified to
    /// 101 characters, and failed inside Postgres, where the error names neither the field nor
    /// anything the person could shorten.
    ///
    /// Truncating rather than refusing is deliberate: the slug is generated for the user, not
    /// supplied by them, so there is nothing for them to correct. The NAME is what they typed and
    /// what gets validated; the slug just has to be a usable, unique segment.
    /// </summary>
    public static string GenerateSlug(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        // Replace special symbols with words
        var str = input.Replace("&", "and").Replace("#", "-sharp");

        // Normalize to FormD to separate diacritics
        str = str.Normalize(NormalizationForm.FormD);

        // Filter out diacritics (non-spacing marks)
        var sb = new StringBuilder();
        foreach (var c in str)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        // Re-normalize, convert to lowercase
        str = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();

        // Replace non-alphanumeric chars (excluding spaces and hyphens) with hyphens
        str = Regex.Replace(str, @"[^a-z0-9\s-]", "-");

        // Replace spaces with hyphens
        str = Regex.Replace(str, @"\s+", "-");

        // Replace multiple consecutive hyphens with a single hyphen
        str = Regex.Replace(str, @"-+", "-");

        // Trim leading and trailing hyphens
        str = str.Trim('-');

        return Shorten(str, WorkspaceConstants.WorkspaceSlugMaxLength);
    }

    /// <summary>
    /// The first slug in this family that no workspace is already using.
    ///
    /// The suffix is what makes this more than a loop. Appending "-1" to a slug already sitting on
    /// the column limit produces one character too many, so the base is shortened to make room for
    /// the suffix BEFORE they are joined — otherwise a perfectly ordinary name would fail only
    /// when it happened to collide, which is the worst kind of bug to reproduce.
    /// </summary>
    public static async Task<string> ResolveSlugCollisionAsync(string baseSlug, IWorkspaceRepository repository, CancellationToken ct = default)
    {
        var currentSlug = Shorten(baseSlug, WorkspaceConstants.WorkspaceSlugMaxLength);
        var counter = 1;

        while (await repository.AnyAsync(w => w.Slug == currentSlug, ct))
        {
            var suffix = $"-{counter}";
            var room = WorkspaceConstants.WorkspaceSlugMaxLength - suffix.Length;
            currentSlug = Shorten(baseSlug, room) + suffix;
            counter++;
        }

        return currentSlug;
    }

    /// <summary>
    /// Cut to <paramref name="maxLength"/> without leaving a trailing hyphen, which would read as
    /// a slug that had been cut off — and, joined to a collision suffix, would double the hyphen.
    /// </summary>
    private static string Shorten(string slug, int maxLength)
    {
        if (maxLength <= 0) return string.Empty;
        if (slug.Length <= maxLength) return slug;

        return slug[..maxLength].TrimEnd('-');
    }
}
