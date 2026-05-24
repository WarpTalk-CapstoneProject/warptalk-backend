using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Domain.Interfaces;

namespace WarpTalk.AuthService.Application.Helpers;

public static class SlugHelper
{
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
        return str.Trim('-');
    }

    public static async Task<string> ResolveSlugCollisionAsync(string baseSlug, IWorkspaceRepository repository, CancellationToken ct = default)
    {
        var currentSlug = baseSlug;
        var counter = 1;

        while (await repository.AnyAsync(w => w.Slug == currentSlug, ct))
        {
            currentSlug = $"{baseSlug}-{counter}";
            counter++;
        }

        return currentSlug;
    }

    public static bool IsValidSlug(string slug, IEnumerable<string> reservedSlugs)
    {
        if (string.IsNullOrWhiteSpace(slug)) return false;

        // Validate length
        if (slug.Length < 3 || slug.Length > 50) return false;

        // Must not start or end with a hyphen
        if (slug.StartsWith('-') || slug.EndsWith('-')) return false;

        // Must not be a reserved keyword
        var reservedSet = new HashSet<string>(reservedSlugs ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (reservedSet.Contains(slug)) return false;

        // Must only contain lowercase a-z, 0-9, and hyphens, and not have double hyphens
        return Regex.IsMatch(slug, @"^[a-z0-9-]+$") && !slug.Contains("--");
    }
}
