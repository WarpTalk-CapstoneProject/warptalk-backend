using System;
using System.Collections.Generic;

namespace WarpTalk.AuthService.Application.Services;

/// <summary>
/// What counts as an avatar, and where it lives.
///
/// WHY THE KEY IS DERIVED AND NOT STORED
///     `users.avatar_url` is the only column there is, and it already holds a Google picture URL
///     for anyone who signed in that way. Adding a second column to remember a storage key would
///     mean a migration for something that can be computed: one user has one avatar, so the key
///     is the user's id and the extension the file arrived with. Uploading again overwrites it,
///     which is what "change my avatar" means.
///
/// WHY THE EXTENSION IS IN THE URL
///     It is what lets the read side answer with the right Content-Type without storing one, and
///     without re-encoding every upload into a single format — which would need an image library
///     this service does not have.
/// </summary>
public static class ProfileAvatarContract
{
    /// <summary>2 MB. An avatar is displayed at 40px; anything larger is being stored for nobody.</summary>
    public const long MaxSizeBytes = 2 * 1024 * 1024;

    /// <summary>The formats a browser will render and this service will accept, by extension.</summary>
    public static readonly IReadOnlyDictionary<string, string> ContentTypeByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["png"] = "image/png",
            ["jpg"] = "image/jpeg",
            ["jpeg"] = "image/jpeg",
            ["webp"] = "image/webp",
        };

    public static string StorageKey(Guid userId, string extension) =>
        $"avatars/{userId}.{extension.ToLowerInvariant()}";

    /// <summary>
    /// The path the browser fetches. Relative on purpose: the API is reached through the gateway
    /// on whatever origin the app is served from, and an absolute URL baked here would be wrong
    /// the moment that origin differs between environments — which it does.
    /// </summary>
    public static string PublicPath(Guid userId, string extension) =>
        $"/api/v1/auth/profile/avatar/{userId}.{extension.ToLowerInvariant()}";

    /// <summary>The extension for an uploaded content type, or null when it is not an image we take.</summary>
    public static string? ExtensionFor(string? contentType)
    {
        var mediaType = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        return mediaType switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/webp" => "webp",
            _ => null,
        };
    }

    /// <summary>
    /// Whether the bytes really are the picture the Content-Type claims.
    ///
    /// The header is supplied by whoever is uploading. Trusting it is how a service ends up
    /// storing a script under a name the browser will happily fetch back, so the first bytes are
    /// read the same way the voice-sample path reads them.
    /// </summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12) return false;

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            return true;
        // JPEG: FF D8 FF
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return true;
        // WebP: "RIFF" .... "WEBP"
        if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
            && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
            return true;

        return false;
    }
}
