using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// A profile picture has to be readable by an &lt;img&gt; tag.
///
/// THE BUG THIS EXISTS FOR
///     Avatar upload shipped in v142 and no avatar has ever appeared. Everything worked: the file
///     reached MinIO, auth.users.avatar_url was written, the web resolved the path against the API
///     origin, and ProfileController.GetAvatar carries [AllowAnonymous] with a comment explaining
///     exactly why it must.
///
///     The gateway answered 401 before any of that ran. `auth-public-route` matches
///     `/api/v1/auth/{endpoint}` — ONE segment — so `/api/v1/auth/profile/avatar/{fileName}`, three
///     deep, fell through to `auth-secure-route` and its RequireAuth policy. An &lt;img&gt; sends no
///     Authorization header and never can, so the browser got a 401, fell back to initials, and the
///     product looked like it had simply ignored the upload.
///
///     [AllowAnonymous] on a controller is invisible to a reverse proxy standing in front of it.
///     That is the whole lesson, and it is why this is asserted here rather than in the auth
///     service's own tests, where it already passes.
///
/// WHAT IT DELIBERATELY EXPOSES
///     Somebody's profile picture, addressed by their own user id, to anyone holding that id. The
///     same posture as the Google-hosted avatar URLs already stored in that column, and the only
///     one under which an image tag can render at all.
/// </summary>
public class AvatarRouteAnonymityTests
{
    private const string AvatarPath = "/api/v1/auth/profile/avatar/{fileName}";

    [Fact]
    public void TheAvatarRouteIsAnonymous()
    {
        var route = AvatarRoute();

        Assert.False(
            route.TryGetProperty("AuthorizationPolicy", out _),
            "The avatar route must carry no AuthorizationPolicy. An <img> tag sends no "
            + "Authorization header, so any policy here renders every avatar in the product as a "
            + "broken image and the fallback initials make it look like the upload was ignored.");
    }

    [Fact]
    public void TheAvatarRouteIsReadOnly()
    {
        var route = AvatarRoute();

        var methods = route.GetProperty("Match").GetProperty("Methods")
            .EnumerateArray()
            .Select(method => method.GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal(new[] { "GET" }, methods);
    }

    [Fact]
    public void TheAvatarRouteOutranksTheAuthenticatedCatchAll()
    {
        using var document = Load();
        var routes = document.RootElement.GetProperty("ReverseProxy").GetProperty("Routes");

        var avatarOrder = routes.GetProperty("auth-avatar-route").GetProperty("Order").GetInt32();
        var secureOrder = routes.GetProperty("auth-secure-route").GetProperty("Order").GetInt32();

        Assert.True(
            avatarOrder < secureOrder,
            $"The avatar route ({avatarOrder}) must be ordered ahead of auth-secure-route "
            + $"({secureOrder}), which matches /api/v1/auth/{{**catch-all}} under RequireAuth and "
            + "would otherwise take the request first.");
    }

    [Fact]
    public void UploadingAnAvatarStillRequiresAuthentication()
    {
        // The read is public; the WRITE must not be. POST goes to /api/v1/auth/profile/avatar —
        // one segment shallower than this route and matched by no anonymous route — so it lands on
        // auth-secure-route. If a future edit widened this match or dropped the method filter,
        // anyone could replace anyone's face.
        var route = AvatarRoute();
        var path = route.GetProperty("Match").GetProperty("Path").GetString();

        Assert.Equal(AvatarPath, path);
        Assert.DoesNotContain("**", path!, StringComparison.Ordinal);
    }

    private static JsonElement AvatarRoute()
    {
        using var document = Load();
        var route = document.RootElement
            .GetProperty("ReverseProxy")
            .GetProperty("Routes")
            .GetProperty("auth-avatar-route");
        // Cloned because the JsonDocument is disposed with the using above, and a JsonElement does
        // not outlive its document.
        return route.Clone();
    }

    private static JsonDocument Load() => JsonDocument.Parse(File.ReadAllText(AppSettingsPath()));

    private static string AppSettingsPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "WarpTalk.Gateway", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find the gateway's appsettings.json.");
    }
}
