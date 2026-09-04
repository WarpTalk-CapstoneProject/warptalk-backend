namespace WarpTalk.AssistantService.Application.Helpers;

/// <summary>
/// The Client Identifier URL rules from OAuth Client ID Metadata Documents, applied to our own
/// configured URL before we present it as a <c>client_id</c>.
/// </summary>
/// <remarks>
/// Every rule here is enforced by shipped authorization servers, so breaking one does not fail
/// locally - it fails as a rejected authorization request against a real provider, which is a far
/// worse place to discover it. Keeping the rules in one helper means the registrar and the
/// conformance test that guards our published document apply exactly the same checks.
/// </remarks>
public static class ClientIdentifierUrl
{
    public static bool IsValid(string? candidate) => Validate(candidate) is null;

    /// <summary>
    /// Returns null when <paramref name="candidate"/> is a usable Client Identifier URL, otherwise
    /// the reason it is not - worth surfacing, because every one of these is a configuration
    /// mistake an operator can fix.
    /// </summary>
    public static string? Validate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return "No client metadata document URL is configured.";

        if (candidate != candidate.Trim() || candidate.Any(IsForbiddenCharacter))
            return "Client Identifier URL contains whitespace, control characters, or a backslash.";

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var url))
            return "Client Identifier URL is not an absolute URL.";

        if (url.Scheme != Uri.UriSchemeHttps)
            return "Client Identifier URL must use HTTPS.";

        if (!string.IsNullOrEmpty(url.UserInfo))
            return "Client Identifier URL must not contain userinfo.";

        if (!string.IsNullOrEmpty(url.Fragment))
            return "Client Identifier URL must not contain a fragment.";

        var path = RawPath(candidate);
        if (string.IsNullOrEmpty(path) || path == "/")
            return "Client Identifier URL must contain a path component; a bare origin is rejected.";

        foreach (var segment in path.Split('/'))
        {
            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(segment);
            }
            catch (UriFormatException)
            {
                return "Client Identifier URL contains invalid percent encoding.";
            }

            if (decoded is "." or "..")
                return "Client Identifier URL must not contain dot path segments.";
        }

        return null;
    }

    /// <summary>
    /// The path taken from the raw string rather than from <see cref="Uri.AbsolutePath"/>.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the check is hand-rolled. <see cref="Uri"/> normalises a path
    /// before exposing it, so <c>https://host/oauth/../secret.json</c> arrives as
    /// <c>/secret.json</c> - the dot segments the specification requires rejecting have already
    /// been collapsed, and a validator reading the parsed path can never see them.
    /// </remarks>
    private static string RawPath(string clientId)
    {
        var schemeEnd = clientId.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return string.Empty;

        var authorityStart = schemeEnd + 3;
        var authority = clientId[authorityStart..];
        var authorityEnd = authority.IndexOfAny(['/', '?', '#']);
        if (authorityEnd < 0) return string.Empty;

        var pathStart = authorityStart + authorityEnd;
        if (clientId[pathStart] != '/') return string.Empty;

        var rest = clientId[pathStart..];
        var pathEnd = rest.IndexOfAny(['?', '#']);
        return pathEnd < 0 ? rest : rest[..pathEnd];
    }

    private static bool IsForbiddenCharacter(char c) =>
        c <= ' ' || (c >= (char)0x7f && c <= (char)0x9f) || c == '\\';
}
