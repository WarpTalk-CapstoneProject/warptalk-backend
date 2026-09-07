using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Tests.Integration;

/// <summary>
/// Who may read a meeting's biên bản, and from which point in its life.
/// </summary>
/// <remarks>
/// <para>
/// The minutes were the LOOSEST read in the whole meeting record, and they are the most formal
/// document in it. <c>GetCurrentAsync</c> gates on <c>RoomReadAccess</c>, which admits the host,
/// every participant AND anyone merely invited by email who never attended — while the transcript,
/// the AI summary and the recording all sit behind the host's Publish switch
/// (<c>ArtifactAccessHelper</c>). Applied to a DRAFT that meant a machine wrote a document with a
/// number and a signature block on it, nobody had checked a word, and it was already readable by
/// more people than the transcript it was drawn from.
/// </para>
/// <para>
/// The rule now: a DRAFT belongs to the people who can act on it, and signing is what publishes
/// it. These go through the real HTTP pipeline against real Postgres because the defect was in the
/// assembled request — the service, the predicate and the controller were each defensible alone.
/// </para>
/// </remarks>
public class MeetingMinutesVisibilityIntegrationTests : BaseIntegrationTest
{
    [Fact]
    public async Task ADraft_IsReadableByTheHost_AndByNobodyElse()
    {
        var host = Guid.NewGuid();
        var participant = Guid.NewGuid();
        const string inviteeEmail = "invitee@example.com";

        var roomId = await CreateRoomAsync(host, invitedEmails: new List<string> { inviteeEmail });
        await JoinAsync(roomId, participant);
        await SeedMinutesAsync(roomId, MeetingMinutesConstants.StatusDraft);

        var asHost = await SendAsync(HttpMethod.Get, $"/api/v1/rooms/{roomId}/minutes", host);
        asHost.StatusCode.Should().Be(HttpStatusCode.OK, "the host is who the draft is FOR");

        // The leak. A participant — and, worse, somebody who was only invited and never came —
        // could read an unchecked machine draft carrying a minutes number and a signature block.
        var asParticipant = await SendAsync(HttpMethod.Get, $"/api/v1/rooms/{roomId}/minutes", participant);
        asParticipant.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var asInvitee = await SendAsync(HttpMethod.Get, $"/api/v1/rooms/{roomId}/minutes", Guid.NewGuid(), inviteeEmail);
        asInvitee.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TheRefusal_SaysItIsADraft_RatherThanThatNothingIsThere()
    {
        // They can see the meeting, so "no minutes" would be a lie they can catch — and it would
        // send them looking for a bug instead of asking the host to sign. Same reasoning as
        // ArtifactAccessHelper.DescribeArtifactDenial.
        var host = Guid.NewGuid();
        var participant = Guid.NewGuid();

        var roomId = await CreateRoomAsync(host);
        await JoinAsync(roomId, participant);
        await SeedMinutesAsync(roomId, MeetingMinutesConstants.StatusDraft);

        var response = await SendAsync(HttpMethod.Get, $"/api/v1/rooms/{roomId}/minutes", participant);

        (await response.Content.ReadAsStringAsync())
            .Should().Contain("draft", "the reason has to name what would change");
    }

    [Fact]
    public async Task SigningPublishesIt_AndTheParticipantCanRead()
    {
        // DRAFT -> IN_REVIEW is a person putting their name to the document. That is the publish
        // act the product already had; it simply governed nothing.
        var host = Guid.NewGuid();
        var participant = Guid.NewGuid();

        var roomId = await CreateRoomAsync(host);
        await JoinAsync(roomId, participant);
        await SeedMinutesAsync(roomId, MeetingMinutesConstants.StatusInReview);

        var response = await SendAsync(HttpMethod.Get, $"/api/v1/rooms/{roomId}/minutes", participant);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AStranger_IsStillRefused_EvenOnceItIsSigned()
    {
        // Publishing widens the audience to the meeting, never past it. The room gate runs first
        // and is unchanged.
        var host = Guid.NewGuid();

        var roomId = await CreateRoomAsync(host);
        await SeedMinutesAsync(roomId, MeetingMinutesConstants.StatusApproved);

        var response = await SendAsync(HttpMethod.Get, $"/api/v1/rooms/{roomId}/minutes", Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "a caller who cannot read the room is not told it exists");
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeded directly, the way <c>ArtifactAccessIntegrationTests</c> seeds a summary: drawing a
    /// draft up through the API would also run the summary lookup and the numbering sequence,
    /// neither of which is what these tests are about.
    /// </summary>
    private async Task SeedMinutesAsync(Guid roomId, string status)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
        var room = await db.TranslationRooms.FindAsync(roomId);

        db.Set<MeetingMinutes>().Add(new MeetingMinutes
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            WorkspaceId = room!.WorkspaceId,
            MinutesNo = $"BB-2026-{Random.Shared.Next(1000, 9999)}",
            Status = status,
            Version = 1,
            IsCurrent = true,
            Content = "{\"agenda\":\"Review the quarter\",\"sections\":[],\"votes\":[]}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    private async Task<Guid> CreateRoomAsync(Guid hostId, List<string>? invitedEmails = null)
    {
        var request = new CreateTranslationRoomRequest(
            WorkspaceId: Guid.NewGuid(),
            Title: "Sprint review",
            Description: null,
            TranslationRoomType: "INSTANT",
            MaxParticipants: 10,
            SourceLanguage: "vi",
            TargetLanguages: new List<string> { "en" },
            Settings: null,
            ScheduledAt: null,
            InvitedEmails: invitedEmails);

        var response = await Client.SendAsync(
            BuildRequest(HttpMethod.Post, "/api/v1/translation-rooms", hostId, body: request));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var room = await response.Content.ReadFromJsonAsync<TranslationRoomDto>();
        return room!.Id;
    }

    private async Task JoinAsync(Guid roomId, Guid userId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
        var room = await db.TranslationRooms.FindAsync(roomId);

        var response = await Client.SendAsync(BuildRequest(
            HttpMethod.Post,
            "/api/v1/translation-rooms/join",
            userId,
            body: new JoinTranslationRoomRequest(room!.TranslationRoomCode, "Participant", "vi", "en")));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid userId, string? email = null) =>
        Client.SendAsync(BuildRequest(method, url, userId, email));

    private static HttpRequestMessage BuildRequest(
        HttpMethod method, string url, Guid userId, string? email = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId.ToString());
        if (email is not null) request.Headers.Add(TestAuthHandler.EmailHeader, email);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        return request;
    }
}
