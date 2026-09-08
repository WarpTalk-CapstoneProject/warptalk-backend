using Microsoft.EntityFrameworkCore;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Tests.Infrastructure;

public sealed class MeetingRuntimeModelContractTests
{
    [Fact]
    public void Model_UsesRtcNamesAndExcludesRetiredCollaborationTables()
    {
        var options = new DbContextOptionsBuilder<MeetingDbContext>()
            .UseNpgsql("Host=localhost;Database=warptalk_meeting")
            .Options;

        using var context = new MeetingDbContext(options);
        var model = context.Model;

        var participant = model.FindEntityType(typeof(RtcStreamParticipant));
        var revocation = model.FindEntityType(typeof(RtcSessionRevocation));

        Assert.NotNull(participant);
        Assert.NotNull(revocation);
        Assert.Equal("rtc_stream_participants", participant.GetTableName());
        Assert.Equal("meeting", participant.GetSchema());
        Assert.Equal("rtc_session_revocations", revocation.GetTableName());
        Assert.Equal("meeting", revocation.GetSchema());

        var mappedTables = model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .Where(table => table is not null)
            .ToHashSet(StringComparer.Ordinal);

        string[] retiredTables =
        [
            "poll_votes",
            "poll_options",
            "polls",
            "question_votes",
            "questions",
            "breakout_assignments",
            "breakout_sessions"
        ];

        Assert.DoesNotContain(retiredTables, mappedTables.Contains);

        var domainTypeNames = typeof(MeetingRoom).Assembly.GetTypes()
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("MeetingParticipant", domainTypeNames);
        Assert.DoesNotContain("MeetingInvitation", domainTypeNames);
    }
}
