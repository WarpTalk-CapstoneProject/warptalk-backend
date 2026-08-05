using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Tests.Workers;

public sealed class IdleRoomMonitoringWorkerPresenceContractTests
{
    /// <summary>
    /// WT-263: the idle reaper and the WT-262 capacity cap must answer "who is in this room" the
    /// same way, from the same definition.
    ///
    /// This test used to pin the literal <c>Status == "CONNECTED" || Status == "JOINED"</c>. The
    /// JOINED half was dead — nothing in the repository has ever written that status; every join
    /// path stores CONNECTED — so the two predicates differed for no reason, which is how a cap and
    /// a reaper drift until one of them ends a live room. The protection is kept, re-anchored to the
    /// shared predicate instead of to a string literal that encoded a false premise.
    /// </summary>
    [Fact]
    public void IdleWorker_SharesTheOneSeatHoldingDefinition()
    {
        var source = File.ReadAllText(FindSourceFile(
            "translation-room/src/WarpTalk.TranslationRoomService.API/Workers/IdleRoomMonitoringWorker.cs"));

        Assert.Contains(
            "TranslationRoomParticipantStatuses.HoldsSeat(p.Status)",
            source,
            StringComparison.Ordinal);

        // A second, private status predicate reappearing here is the regression this guards.
        Assert.DoesNotContain("p.Status == \"", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shared definition itself: CONNECTED only. Ratified product decision (owner, 2026-08-05)
    /// that a lobby participant does not hold a seat, so WAITING must stay out of it.
    /// </summary>
    [Fact]
    public void SeatHolding_IsConnectedOnly_SoTheLobbyDoesNotOccupyASeat()
    {
        Assert.Equal(
            new[] { nameof(TranslationRoomParticipantStatus.CONNECTED) },
            TranslationRoomParticipantStatuses.SeatHolding);

        Assert.False(TranslationRoomParticipantStatuses.HoldsSeat(
            nameof(TranslationRoomParticipantStatus.WAITING)));
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
