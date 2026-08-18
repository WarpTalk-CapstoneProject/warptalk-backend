using System;
using System.IO;
using System.Linq;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests;

/// <summary>
/// WT-415 — MEETING_INVITED is fully wired and has never fired in production.
///
/// notification.notification_messages, grouped by type:
///
///     MEETING_STARTED         127
///     MEETING_SUMMARY_READY    81
///     MEETING_REMINDER         20
///     MEETING_INVITED           0     <-- against 538 invitation rows
///
/// Its siblings work. It is not throwing — its own catch has never logged. So it returns at one
/// of two guards in NotifyInvitedUserAsync, and until now neither wrote anything down, which is
/// why this investigation had nothing to start from.
///
/// This is a source check rather than a behavioural one on purpose: the two guards are inside a
/// private method reached only through room creation with a live gRPC pair, and what needs
/// pinning is that NEITHER EXIT IS SILENT. A behavioural test would need the whole service stood
/// up to assert the absence of silence, which is a lot of machinery to protect one log line that
/// a future tidy-up would delete without noticing.
/// </summary>
public class InviteNotificationInstrumentationTests
{
    private static string ServiceSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "translation-room")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var path = Path.Combine(
            dir!.FullName,
            "translation-room", "src", "WarpTalk.TranslationRoomService.Application",
            "Services", "TranslationRoomService.cs");

        Assert.True(File.Exists(path), $"could not locate TranslationRoomService.cs (looked at {path})");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Every way the bell can be skipped must name itself. The reasons are distinct because they
    /// point at different things: one is a wiring/deployment fault, the other is an invitee who
    /// genuinely has no account and whose only channel is the email.
    /// </summary>
    [Theory]
    [InlineData("reason=clients_unavailable")]
    [InlineData("reason=no_account_for_email")]
    public void EverySilentSkipNamesItself(string reason)
    {
        Assert.Contains(
            reason,
            ServiceSource());
    }

    /// <summary>
    /// And the success side, so a notification that fired but never arrived can be told apart
    /// from one that never fired. Without it, a missing MEETING_INVITED row is unattributable.
    /// </summary>
    [Fact]
    public void TheSuccessfulSendIsAlsoRecorded()
    {
        Assert.Contains("invite_notification_sent", ServiceSource());
    }

    /// <summary>
    /// The guards must stay guards. Instrumenting them must not have turned either into a path
    /// that carries on and sends a notification with no user id.
    /// </summary>
    [Fact]
    public void BothGuardsStillReturn()
    {
        var source = ServiceSource();
        var start = source.IndexOf("private async Task NotifyInvitedUserAsync", StringComparison.Ordinal);
        Assert.True(start > 0, "NotifyInvitedUserAsync not found");

        var method = source.Substring(start, Math.Min(3000, source.Length - start));

        var skipLogs = method.Split("invite_notification_skipped").Length - 1;
        Assert.Equal(2, skipLogs);

        // Each skip log is followed by a return before the gRPC send.
        foreach (var segment in method.Split("invite_notification_skipped").Skip(1))
        {
            var untilSend = segment.IndexOf("SendNotificationAsync", StringComparison.Ordinal);
            var untilReturn = segment.IndexOf("return;", StringComparison.Ordinal);
            Assert.True(untilReturn >= 0, "a skip log has no return after it");
            Assert.True(
                untilSend < 0 || untilReturn < untilSend,
                "a skip path reaches SendNotificationAsync instead of returning");
        }
    }
}
