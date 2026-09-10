using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using WarpTalk.Gateway.Hubs;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// WT-605 (security). Transcript text has one legitimate origin, and it is not a hub method.
/// </summary>
/// <remarks>
/// <c>SendTranscriptSegment(Guid, TranscriptSegmentDto)</c> used to broadcast
/// "TranscriptSegmentReceived" to the entire room from whatever the caller handed it, behind
/// nothing but the hub's class-level [Authorize]. No membership check, no host check. Any signed-in
/// account that knew a room id could put sentences in a named speaker's mouth in front of everyone
/// — and could do it during a pause, walking around every gate this ticket added at once.
///
/// It was removed rather than authorized. Even host-only it would let a person hand-author lines
/// attributed to someone else into a record that gets exported, signed into minutes and read back
/// weeks later. The AI pipeline never needed it: segments reach clients through
/// AiResultConsumerService's stt:results consumer, and a sweep of warptalk-web and warptalk-desktop
/// found no caller.
///
/// A reflection test rather than a behavioural one because the contract being defended is the
/// absence of a door. There is nothing left to exercise; what has to stay true is that nobody adds
/// it back on the way to solving something else.
/// </remarks>
public sealed class TranslationRoomHubTranscriptInjectionTests
{
    [Fact]
    public void TheHubExposesNoWayForAClientToInjectATranscriptSegment()
    {
        var clientInvokable = typeof(TranslationRoomHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain("SendTranscriptSegment", clientInvokable);

        // Named separately from the assertion above so a rename cannot slip the same capability
        // back in under a different word. Anything a client can invoke that ends up broadcasting a
        // transcript segment belongs on the pipeline side of Redis, not on this hub.
        Assert.DoesNotContain(
            clientInvokable,
            name => name.Contains("TranscriptSegment", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The hub still requires authentication at the class level. Removing one method must not have
    /// been the moment somebody decided the attribute was decorative.
    /// </summary>
    [Fact]
    public void TheHubStillRequiresAuthentication()
    {
        Assert.NotNull(
            typeof(TranslationRoomHub)
                .GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>());

        Assert.True(typeof(Hub).IsAssignableFrom(typeof(TranslationRoomHub)));
    }
}
