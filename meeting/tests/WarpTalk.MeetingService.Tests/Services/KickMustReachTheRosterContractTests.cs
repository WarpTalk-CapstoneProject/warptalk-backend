using System.Text.RegularExpressions;

namespace WarpTalk.MeetingService.Tests.Services;

/// <summary>
/// WT-564: the kick reaches the room service FIRST, and a failure there aborts the kick.
///
/// This service's kick evicts from LiveKit, deactivates its own participant row and revokes the
/// meeting invitation. Every one of those stops the person being in the meeting NOW, and not one
/// of them stops them coming back: the TERMINAL status is KICKED on the ROOM service, which is
/// what its join refuses on. Without that write the roster row stayed CONNECTED, became
/// DISCONNECTED when the socket dropped, and the room service's rejoin path reads DISCONNECTED as
/// proof of admission — straight back in, not even a lobby.
///
/// ORDER IS THE POINT, and it is why this is asserted rather than left to review.
///
/// Placed last, a failed RPC would leave somebody evicted from LiveKit and revoked locally who can
/// still walk back in through the other service, with the host having seen "kicked". Placed first,
/// a failure changes nothing and the host sees the kick refused — which is a state they can act
/// on. This unit of work spans two services and has no transaction, so the only protection is
/// doing the irreversible-looking thing after the one that can be refused.
///
/// Asserted on the source because it is an ORDER of two calls, and because a mocked test of this
/// method would need the whole service graph to say something a reader can check in four lines.
/// </summary>
public sealed class KickMustReachTheRosterContractTests
{
    private const string ServicePath =
        "meeting/src/WarpTalk.MeetingService.Application/Services/MeetingRoomService.cs";

    [Fact]
    public void TheRosterKickComesBeforeAnyLocalWrite()
    {
        var body = KickMethodBody();

        var rosterKick = body.IndexOf("KickRoomParticipantAsync", StringComparison.Ordinal);
        var livekitRemoval = body.IndexOf("RemoveParticipantAsync", StringComparison.Ordinal);
        var save = body.IndexOf("SaveChangesAsync", StringComparison.Ordinal);

        Assert.True(rosterKick > 0, "The kick must reach the room service.");
        Assert.True(livekitRemoval > 0, "The kick must still evict from LiveKit.");
        Assert.True(save > 0, "The kick must still persist its local changes.");

        Assert.True(
            rosterKick < save,
            "The roster kick must come before this service commits anything, so a refusal leaves "
                + "nothing half-applied.");
        Assert.True(
            rosterKick < livekitRemoval,
            "The roster kick must come before the LiveKit eviction — evicting somebody who can "
                + "then rejoin is the exact failure this ordering prevents.");
    }

    [Fact]
    public void AFailedRosterKickAbortsTheWholeKick()
    {
        // Ignoring the result is the tempting shortcut: the person does leave the meeting, so it
        // looks like it worked. It just does not stay done.
        var body = KickMethodBody();

        // Bound to the roster call's OWN result variable, and to the window before the next await.
        // A looser `if (!x.IsSuccess) return` anywhere after the call matches the LiveKit removal's
        // guard further down the method — which is how the first version of this assertion passed
        // with the roster check deleted.
        var assignment = Regex.Match(
            body, @"var (?<name>\w+) = await _grpcService\.KickRoomParticipantAsync");
        Assert.True(assignment.Success, "The roster kick must be awaited into a result.");

        var name = Regex.Escape(assignment.Groups["name"].Value);
        var afterCall = body[(assignment.Index + assignment.Length)..];
        var nextAwait = afterCall.IndexOf("await ", StringComparison.Ordinal);
        var window = nextAwait > 0 ? afterCall[..nextAwait] : afterCall;

        Assert.Matches($@"if \(!{name}\.IsSuccess\)\s*\r?\n?\s*return Result\.Failure", window);
    }

    private static string KickMethodBody()
    {
        var source = Source(ServicePath);
        var code = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        code = Regex.Replace(code, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);

        var declaration = Regex.Match(code, @"public\s+async\s+Task[^\n]*?\bKickParticipantAsync\s*\(");
        Assert.True(declaration.Success, "KickParticipantAsync declaration not found.");

        var start = declaration.Index;
        var next = Regex.Match(code[(start + declaration.Length)..], @"\n    public\s");
        return next.Success
            ? code.Substring(start, declaration.Length + next.Index)
            : code[start..];
    }

    private static string Source(string relativePath)
    {
        foreach (var startDir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(startDir);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Could not locate {relativePath}.");
    }
}
