using System.Text.RegularExpressions;

namespace WarpTalk.MeetingService.Tests.Workers;

/// <summary>
/// The two WarpBot surfaces consume ONE stream, and must not disagree about what is on it.
///
/// `assistant:chat_results` is published once by ai_assistant_worker and read by two independent
/// consumer groups: the assistant service, which drives the global widget, and the meeting
/// service, which drives the in-meeting chat. Each is internally consistent, so a type that only
/// one of them handles is invisible to any test of either — and drift is exactly what happened.
///
/// The meeting consumer never handled `tool_call_completed`. It fell through to the terminal
/// check ("tool_call_completed" is not "completed") and was dropped. That single gap discarded a
/// meeting's entire record of a web-search turn, because OpenAI's HOSTED web search never enters
/// the worker's dispatch loop: no function call is dispatched for it, the worker publishes the
/// step by hand off the response stream, and the event that carries the searched target is the
/// COMPLETED one — the started event fires before the item naming the query is on the wire.
///
/// Reported from production as "the widget shows every step and which sites it searched, the
/// meeting chat sits on Reading your question".
/// </summary>
public sealed class AssistantStepParityContractTests
{
    private const string MeetingConsumer =
        "meeting/src/WarpTalk.MeetingService.API/HostedServices/MeetingChatAssistantResultConsumerService.cs";

    private const string WidgetConsumer =
        "assistant/src/WarpTalk.AssistantService.API/Services/AssistantChatResultConsumerService.cs";

    /// <summary>
    /// Every result type the widget acts on, the meeting must act on too. Named rather than
    /// derived so that adding one to the widget alone fails here instead of shipping a surface
    /// that silently shows less.
    /// </summary>
    [Theory]
    [InlineData("chunk")]
    [InlineData("tool_call_started")]
    [InlineData("tool_call_completed")]
    [InlineData("reasoning")]
    [InlineData("completed")]
    [InlineData("failed")]
    public void BothConsumers_ActOnTheSameResultTypes(string resultType)
    {
        Assert.Contains(resultType, AdmittedTypes(WidgetConsumer));
        Assert.Contains(resultType, AdmittedTypes(MeetingConsumer));
    }

    /// <summary>
    /// The types a consumer actually DISPATCHES ON — read off its guards, not off the file.
    ///
    /// Searching the source for the literal is not good enough, and this is not hypothetical:
    /// dropping "tool_call_completed" back out of the meeting consumer's guard leaves the string
    /// behind in the now-unreachable block below it, so a `Contains` check passes against the
    /// exact bug it exists to catch. Comments are stripped for the same reason — the one above
    /// that guard explains the defect using the type's own name.
    /// </summary>
    private static IReadOnlyCollection<string> AdmittedTypes(string relativePath)
    {
        var source = File.ReadAllText(FindSourceFile(relativePath));
        var code = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        code = Regex.Replace(code, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);

        var admitted = new HashSet<string>(StringComparer.Ordinal);

        // `case "chunk":` — the widget's switch.
        foreach (Match match in Regex.Matches(code, @"case\s+""(?<type>[a-z_]+)""\s*:"))
            admitted.Add(match.Groups["type"].Value);

        // `if (resultType is "a" or "b")` and `if (resultType == "a")` — the meeting's guards.
        //
        // DISPATCH-LEVEL ONLY, which is what the eight-space indent pins. A guard nested INSIDE
        // a branch decides what to do with a type that was already admitted; it does not admit
        // anything. Counting those is not a theoretical weakness — it is the second way this
        // check passed against the real bug: with "tool_call_completed" removed from the outer
        // guard, the inner `resultType == "tool_call_completed"` that picks the broadcast is
        // still there, now unreachable, and still looks like a dispatch.
        //
        // A NEGATED guard is an exit, not an admission: `is not ("completed" or "failed")`
        // returns early for everything else, so the types it names are the ones that continue.
        foreach (Match match in Regex.Matches(
            code,
            @"^ {8}if \(resultType\s+(?:is|==)\s+(?<expr>not\s*\([^)]*\)|[^)\r\n]+)",
            RegexOptions.Multiline))
        {
            foreach (Match literal in Regex.Matches(match.Groups["expr"].Value, @"""(?<type>[a-z_]+)"""))
                admitted.Add(literal.Groups["type"].Value);
        }

        return admitted;
    }

    /// <summary>
    /// Naming the type is not acting on it — the string could sit in a comment or a log line.
    /// The meeting consumer has to actually put the finished step on the wire.
    /// </summary>
    [Fact]
    public void MeetingConsumer_BroadcastsAFinishedToolCall()
    {
        var meeting = File.ReadAllText(FindSourceFile(MeetingConsumer));

        Assert.Contains("BroadcastAssistantToolCallCompletedAsync(", meeting, StringComparison.Ordinal);
    }

    /// <summary>
    /// A finished tool call must not be relayed as a STARTED one.
    ///
    /// It is the shortcut this fix invites — the client already binds the started event, so
    /// reusing it is one line rather than three. But the client folds a completed step into the
    /// step already running for that tool, filling in a target the started event could not carry;
    /// relayed as another "started" the same search is drawn twice, once with no target and once
    /// with it.
    /// </summary>
    [Fact]
    public void AFinishedToolCall_IsNotRelayedAsAStartedOne()
    {
        var meeting = File.ReadAllText(FindSourceFile(MeetingConsumer));
        var completedBranch = meeting.IndexOf(
            "resultType == \"tool_call_completed\"", StringComparison.Ordinal);

        Assert.True(completedBranch > 0, "The completed branch must exist.");

        // The next broadcast after that test is the one the branch performs.
        var nextBroadcast = meeting.IndexOf(
            "Broadcast", completedBranch, StringComparison.Ordinal);
        Assert.True(nextBroadcast > 0, "The completed branch must broadcast something.");
        Assert.StartsWith(
            "BroadcastAssistantToolCallCompletedAsync",
            meeting[nextBroadcast..],
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Assistant broadcasts must address the group the CLIENT actually joins.
    ///
    /// MeetingChatHub.JoinMeetingRoom takes the TRANSLATION room id — the one in the URL — looks
    /// the meeting room up by it, and joins `meeting_chat:{translationRoomId}`. MeetingChatService
    /// broadcasts on that same value because it is what the caller passed. This consumer had only
    /// `request.MeetingRoomId`, the meeting room's own primary key, and broadcast on that.
    ///
    /// The two are different values for the same room, so every assistant event — pending, each
    /// tool call, each reasoning line, each chunk, and the answer — was addressed to a group name
    /// no client had ever joined. Nothing errored: SignalR delivers to an empty group happily.
    /// Human chat messages were unaffected, which is exactly why it looked like an assistant bug.
    ///
    /// Verified in production: for one turn the request's meeting_room_id was
    /// 01a02c69-4355-7486-9da6-f404108fc0b1 while the room the user had open — and had joined the
    /// hub group with — was 01a02c69-1636-7839-9857-be0837258b43.
    /// </summary>
    [Fact]
    public void AssistantBroadcasts_AddressTheGroupTheClientJoined()
    {
        var meeting = File.ReadAllText(FindSourceFile(MeetingConsumer));
        var code = Regex.Replace(meeting, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        code = Regex.Replace(code, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);

        // The primary key must never be handed to a Broadcast call. Comments are stripped first
        // because the one above the lookup explains the defect using that very expression.
        var broadcastsOnThePrimaryKey = Regex.Matches(
            code, @"Broadcast\w+Async\([^;]*request\.MeetingRoomId", RegexOptions.Singleline);

        Assert.True(
            broadcastsOnThePrimaryKey.Count == 0,
            "Assistant broadcasts must use the room's TranslationRoomId — the meeting room's "
                + "primary key names a hub group no client ever joins.");

        // And the translation id has to actually be resolved, not merely absent.
        Assert.Contains("TranslationRoomId", code, StringComparison.Ordinal);

        // The group id must come from the room ALONE. A fallback — `room.TranslationRoomId != ...
        // ? ... : request.MeetingRoomId` — restores the exact silent failure while keeping every
        // Broadcast call looking correct, which is how it slipped past the check above.
        var assignment = Regex.Match(code, @"var groupRoomId\s*=\s*(?<expr>[^;]+);");
        Assert.True(assignment.Success, "The group id must be resolved into groupRoomId.");
        Assert.DoesNotContain(
            "request.MeetingRoomId",
            assignment.Groups["expr"].Value,
            StringComparison.Ordinal);
    }

    private static string FindSourceFile(string relativePath)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Could not locate {relativePath}.");
    }
}
