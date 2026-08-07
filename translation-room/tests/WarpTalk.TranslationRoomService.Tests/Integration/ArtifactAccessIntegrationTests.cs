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
/// The artifact-access policy, end to end: real HTTP pipeline, real authorization, real Postgres.
/// </summary>
/// <remarks>
/// <para>
/// These go through the API rather than calling the service directly on purpose. Every one of the
/// three defects they cover survived because the pieces were each individually defensible and only
/// the assembled request was wrong — the download endpoint refused a participant while the list
/// endpoint handed the same participant the whole summary body; the policy comparison was against
/// a vocabulary no writer produced, which a test feeding the helper its own invented strings would
/// never catch. So the artifact rows are seeded the way the pipeline seeds them, the room is
/// created and joined through the API, and the assertions are about response bodies.
/// </para>
/// </remarks>
public class ArtifactAccessIntegrationTests : BaseIntegrationTest
{
    private const string SummaryJson =
        "{\"summary\":\"Confidential overview\",\"decisions\":[\"Ship on Friday\"],\"actionItems\":[\"Tu to sign off\"]}";

    [Fact]
    public async Task ArtifactList_OnHostOnlyRoom_GivesTheSummaryBodyToTheHostAndToNobodyElse()
    {
        var host = Guid.NewGuid();
        var participant = Guid.NewGuid();
        const string inviteeEmail = "invitee@example.com";

        var roomId = await CreateRoomAsync(host, invitedEmails: new List<string> { inviteeEmail });
        await JoinAsync(roomId, participant);
        await SeedSummaryArtifactAsync(roomId);

        // The room is HOST_ONLY — nothing set it, which is the point: that is the default every
        // room is created with, and the state the demo script describes.
        (await GetSettingsAsync(roomId, host)).ArtifactAccess
            .Should().Be(ArtifactAccessLevels.HostOnly);

        var asHost = await GetArtifactsAsync(roomId, host);
        asHost.Should().ContainSingle();
        asHost[0].Content.Should().Be(SummaryJson, "the host owns the room and its artifacts");

        // The leak. A participant of a HOST_ONLY room used to receive this body in full — Overview,
        // Decisions, Action items — while /download correctly refused them, which is exactly why
        // the policy looked enforced.
        var asParticipant = await GetArtifactsAsync(roomId, participant);
        asParticipant.Should().ContainSingle("a participant may still see THAT an artifact exists");
        asParticipant[0].Content.Should().BeNull("but not what is in it");
        asParticipant[0].Type.Should().Be("SUMMARY_EXPORT");
        asParticipant[0].Status.Should().Be("COMPLETED");

        // And the widest caller of all: someone who was merely invited by email and never joined.
        // Room-read admits them (RoomReadAccess counts a PENDING invitation), so they reached this
        // list and got the summary of a meeting they never attended.
        var asInvitee = await GetArtifactsAsync(roomId, Guid.NewGuid(), inviteeEmail);
        asInvitee.Should().ContainSingle();
        asInvitee[0].Content.Should().BeNull();
    }

    [Fact]
    public async Task ArtifactList_OnAllParticipantsRoom_GivesTheSummaryBodyToParticipantsButNotToAnInvitee()
    {
        var host = Guid.NewGuid();
        var participant = Guid.NewGuid();
        const string inviteeEmail = "invitee-2@example.com";

        var roomId = await CreateRoomAsync(host, invitedEmails: new List<string> { inviteeEmail });
        await JoinAsync(roomId, participant);
        await SeedSummaryArtifactAsync(roomId);

        (await SetArtifactAccessAsync(roomId, host, ArtifactAccessLevels.AllParticipants))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Before this fix ALL_PARTICIPANTS did nothing at all: the guard compared against
        // "Participants", a string no writer has ever produced, so the host could open the room to
        // its participants and every one of them stayed refused forever.
        var asParticipant = await GetArtifactsAsync(roomId, participant);
        asParticipant[0].Content.Should().Be(SummaryJson);

        // ALL_PARTICIPANTS means participants, not "anyone the room-read gate lets through". An
        // unaccepted invitation is not attendance.
        var asInvitee = await GetArtifactsAsync(roomId, Guid.NewGuid(), inviteeEmail);
        asInvitee.Should().ContainSingle();
        asInvitee[0].Content.Should().BeNull();
    }

    [Fact]
    public async Task ArtifactDownload_FollowsTheSamePolicyAsTheList()
    {
        var host = Guid.NewGuid();
        var participant = Guid.NewGuid();

        var roomId = await CreateRoomAsync(host);
        await JoinAsync(roomId, participant);
        var artifactId = await SeedSummaryArtifactAsync(roomId);

        var refused = await SendAsync(HttpMethod.Get, $"/api/v1/room-artifacts/{artifactId}/download", participant);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the room is HOST_ONLY");

        (await SetArtifactAccessAsync(roomId, host, ArtifactAccessLevels.AllParticipants))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var allowed = await SendAsync(HttpMethod.Get, $"/api/v1/room-artifacts/{artifactId}/download", participant);
        allowed.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "ALL_PARTICIPANTS has never actually permitted anything until now");
        (await allowed.Content.ReadAsStringAsync()).Should().Contain("Confidential overview");
    }

    [Theory]
    // The two spellings the old guard compared against. They were never writable values; storing
    // one produced a room that silently denied everybody, which is how the mismatch stayed hidden.
    [InlineData("Participants")]
    [InlineData("Workspace")]
    [InlineData("ALL PARTICIPANTS")]
    [InlineData("all_participants")]
    [InlineData("")]
    public async Task SettingsUpdate_RejectsAnArtifactAccessLevelTheGuardCannotEnforce(string level)
    {
        var host = Guid.NewGuid();
        var roomId = await CreateRoomAsync(host);

        var response = await SetArtifactAccessAsync(roomId, host, level);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("not supported");

        // Rejected on the way in means the stored policy is still one the guard can act on.
        (await GetSettingsAsync(roomId, host)).ArtifactAccess
            .Should().Be(ArtifactAccessLevels.HostOnly);
    }

    [Fact]
    public async Task RoomCreate_RejectsAnArtifactAccessLevelTheGuardCannotEnforce()
    {
        var response = await Client.SendAsync(BuildRequest(
            HttpMethod.Post,
            "/api/v1/translation-rooms",
            Guid.NewGuid(),
            body: NewRoomRequest(new RoomSettingsRequest(ArtifactAccess: "Participants"))));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("not supported");
    }

    [Fact]
    public async Task RecordingConsent_CanOnlyBeGrantedByTheHost()
    {
        var host = Guid.NewGuid();
        var participant = Guid.NewGuid();

        var roomId = await CreateRoomAsync(host);
        await JoinAsync(roomId, participant);
        // ConsentRequired = true is what RecordingCompletedEventProcessor writes for every
        // recording, so this is the live shape, not a hypothetical one.
        var artifactId = await SeedRecordingArtifactAsync(roomId, consentRequired: true);

        // Open the room to participants so the ONLY thing standing between this participant and
        // the recording is the consent hold. Otherwise a 403 here would prove nothing about
        // consent — it would just be the artifact-access policy refusing them.
        (await SetArtifactAccessAsync(roomId, host, ArtifactAccessLevels.AllParticipants))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var blocked = await SendAsync(HttpMethod.Get, $"/api/v1/room-artifacts/{artifactId}/download", participant);
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // Both refusals are 403, so assert on the reason: this must be the consent hold, not the
        // artifact-access policy. Otherwise the "host grants, participant downloads" step below
        // would prove nothing about consent at all.
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("Consent is required");

        // The defect: this used to authorize with the very same predicate as the download check,
        // so the participant just refused above could grant their own consent, get a 204, and come
        // straight back for the file.
        var selfGrant = await SendAsync(HttpMethod.Post, $"/api/v1/room-artifacts/{artifactId}/consent", participant);
        selfGrant.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadConsentRequiredAsync(artifactId)).Should().BeTrue("a refused POST must not have written");

        var hostGrant = await SendAsync(HttpMethod.Post, $"/api/v1/room-artifacts/{artifactId}/consent", host);
        hostGrant.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterConsent = await SendAsync(HttpMethod.Get, $"/api/v1/room-artifacts/{artifactId}/download", participant);
        afterConsent.StatusCode.Should().Be(HttpStatusCode.OK);

        // Stated, not fixed: consent is one boolean on the shared row, so the host's single grant
        // released the recording for EVERY participant at once. Pinned here so the day someone
        // adds per-user grants, this assertion is what tells them the semantics changed.
        var otherParticipant = Guid.NewGuid();
        await JoinAsync(roomId, otherParticipant);
        var neverAsked = await SendAsync(HttpMethod.Get, $"/api/v1/room-artifacts/{artifactId}/download", otherParticipant);
        neverAsked.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "consent remains GLOBAL — one grant unlocks everyone, and that is a known, deliberate limitation");
    }

    // ---- harness ----------------------------------------------------------------------------

    private static CreateTranslationRoomRequest NewRoomRequest(RoomSettingsRequest? settings = null) =>
        new(
            WorkspaceId: Guid.NewGuid(),
            Title: "Artifact access room",
            Description: null,
            TranslationRoomType: "INSTANT",
            MaxParticipants: 10,
            SourceLanguage: "en",
            TargetLanguages: new List<string> { "vi" },
            Settings: settings,
            ScheduledAt: null,
            InvitedEmails: null);

    private async Task<Guid> CreateRoomAsync(Guid hostId, List<string>? invitedEmails = null)
    {
        var request = NewRoomRequest() with { InvitedEmails = invitedEmails };
        var response = await Client.SendAsync(
            BuildRequest(HttpMethod.Post, "/api/v1/translation-rooms", hostId, body: request));

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        var room = await response.Content.ReadFromJsonAsync<TranslationRoomDto>();
        return room!.Id;
    }

    private async Task JoinAsync(Guid roomId, Guid userId)
    {
        var code = await ReadRoomCodeAsync(roomId);
        var response = await Client.SendAsync(BuildRequest(
            HttpMethod.Post,
            "/api/v1/translation-rooms/join",
            userId,
            body: new JoinTranslationRoomRequest(code, "Participant", "vi", "en")));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<List<TranslationRoomArtifactDto>> GetArtifactsAsync(
        Guid roomId, Guid userId, string? email = null)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/translation-rooms/{roomId}/artifacts", userId, email);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<List<TranslationRoomArtifactDto>>())!;
    }

    private async Task<RoomSettingsResponse> GetSettingsAsync(Guid roomId, Guid userId)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/translation-rooms/{roomId}", userId);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var room = await response.Content.ReadFromJsonAsync<TranslationRoomDto>();
        return room!.Settings!;
    }

    private Task<HttpResponseMessage> SetArtifactAccessAsync(Guid roomId, Guid hostId, string level) =>
        Client.SendAsync(BuildRequest(
            HttpMethod.Put,
            $"/api/v1/translation-rooms/{roomId}/settings",
            hostId,
            body: new UpdateRoomSettingsRequest(
                Title: null,
                Description: null,
                MaxParticipants: null,
                ScheduledAt: null,
                InvitedEmails: null,
                Settings: new RoomSettingsRequest(ArtifactAccess: level),
                SourceLanguage: null,
                TargetLanguages: null)));

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

    private async Task<string> ReadRoomCodeAsync(Guid roomId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
        var room = await db.TranslationRooms.FindAsync(roomId);
        return room!.TranslationRoomCode;
    }

    /// <summary>
    /// Seeded directly, because that is how a summary really arrives: the AI pipeline writes it,
    /// and there is no client-facing endpoint that creates one.
    /// </summary>
    private Task<Guid> SeedSummaryArtifactAsync(Guid roomId) =>
        SeedArtifactAsync(new TranslationRoomArtifact
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = roomId,
            ArtifactType = "SUMMARY_EXPORT",
            FileFormat = "json",
            Content = SummaryJson,
            Status = "COMPLETED",
            CreatedAt = DateTime.UtcNow
        });

    private Task<Guid> SeedRecordingArtifactAsync(Guid roomId, bool consentRequired) =>
        SeedArtifactAsync(new TranslationRoomArtifact
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = roomId,
            ArtifactType = "OPTIONAL_RECORDING",
            FileFormat = "markdown",
            Content = "Confidential overview",
            ContainsRawAudio = true,
            ContainsRawVideo = true,
            ConsentRequired = consentRequired,
            Status = "COMPLETED",
            CreatedAt = DateTime.UtcNow
        });

    private async Task<Guid> SeedArtifactAsync(TranslationRoomArtifact artifact)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
        db.TranslationRoomArtifacts.Add(artifact);
        await db.SaveChangesAsync();
        return artifact.Id;
    }

    private async Task<bool> ReadConsentRequiredAsync(Guid artifactId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
        var artifact = await db.TranslationRoomArtifacts.FindAsync(artifactId);
        return artifact!.ConsentRequired;
    }
}
