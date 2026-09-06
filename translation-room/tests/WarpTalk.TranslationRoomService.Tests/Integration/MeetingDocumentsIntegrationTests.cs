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
/// The meeting-documents read, end to end against real Postgres.
/// </summary>
/// <remarks>
/// <para>
/// Integration rather than unit, and that is the whole point of the file. The read unions two
/// tables with <c>Concat</c> and then orders and pages OVER the union — none of which a mocked
/// <c>IQueryable</c> exercises. This project has already shipped a query that compiled, passed six
/// mocked tests, and answered 500 to every real call because EF could not translate its ORDER BY.
/// A test that does not touch a database cannot tell the difference, so this one does.
/// </para>
/// <para>
/// The second thing under test is that the document list cannot become a looser way to read a
/// meeting than the meeting list is. Documents are joined onto the same authorized rooms query the
/// archive uses, so a document of an invisible meeting must not appear at all — and a body the
/// download endpoint would refuse must be flagged, not silently offered.
/// </para>
/// </remarks>
public class MeetingDocumentsIntegrationTests : BaseIntegrationTest
{
    private const string SummaryJson =
        "{\"summary\":\"Quarterly review\",\"decisions\":[\"Ship on Friday\"],\"actionItems\":[]}";

    /// <summary>Byte for byte what the summary worker writes when nobody spoke.</summary>
    private const string InsufficientSummaryJson =
        "{\"summary\":\"The AI assistant could not generate a summary for this meeting "
        + "(no transcript content was available or generation did not complete in time).\","
        + "\"decisions\":[],\"actionItems\":[],\"insufficientData\":true}";

    [Fact]
    public async Task Documents_UnionsArtifactsAndMinutes_AndPagesOverTheUnion()
    {
        var host = Guid.NewGuid();
        var roomId = await CreateRoomAsync(host);

        await SeedArtifactAsync(roomId, "TRANSCRIPT_EXPORT", "markdown");
        await SeedArtifactAsync(roomId, "SUMMARY_EXPORT", "json", SummaryJson);
        await SeedArtifactAsync(roomId, "OPTIONAL_RECORDING", "mp4");
        await SeedMinutesAsync(roomId, host, "BB-2026-0001");

        // The assertion that matters: this returns at all. A translation failure here is a 500,
        // which is exactly how the previous ORDER BY defect reached production.
        var page = await GetDocumentsAsync(host);

        page.Total.Should().Be(4, "three artifacts and one minutes document belong to this meeting");
        page.Documents.Should().HaveCount(4);
        page.Documents.Select(d => d.Type).Should().BeEquivalentTo(new[]
        {
            "TRANSCRIPT_EXPORT", "SUMMARY_EXPORT", "RECORDING", "MINUTES"
        }, "the stored OPTIONAL_RECORDING is reported under the name the web already uses");

        // Paging is over the UNION, not over either half: a page size of 3 must leave exactly one
        // document behind regardless of which table it came from.
        var first = await GetDocumentsAsync(host, pageSize: 3);
        first.Documents.Should().HaveCount(3);
        first.Total.Should().Be(4, "total counts the whole union, never the page");

        var second = await GetDocumentsAsync(host, page: 2, pageSize: 3);
        second.Documents.Should().HaveCount(1);

        first.Documents.Select(d => d.Id)
            .Should().NotIntersectWith(second.Documents.Select(d => d.Id),
                "a document must not appear on two pages");
    }

    [Fact]
    public async Task Documents_CarryTheMinutesIdentity_AndWhetherTheMeetingAlreadyHasMinutes()
    {
        var host = Guid.NewGuid();
        var withMinutes = await CreateRoomAsync(host);
        var withoutMinutes = await CreateRoomAsync(host);

        await SeedArtifactAsync(withMinutes, "SUMMARY_EXPORT", "json", SummaryJson);
        await SeedMinutesAsync(withMinutes, host, "BB-2026-0007");
        await SeedArtifactAsync(withoutMinutes, "SUMMARY_EXPORT", "json", SummaryJson);

        var documents = (await GetDocumentsAsync(host)).Documents;

        var minutes = documents.Single(d => d.Type == "MINUTES");
        minutes.MinutesNo.Should().Be("BB-2026-0007");
        minutes.MinutesVersion.Should().Be(1);
        minutes.Status.Should().Be(MeetingMinutesConstants.StatusDraft);

        // The field the grid's "draw up the minutes" offer hangs on. Minutes have been buildable
        // for weeks and the table is empty because the only door was four clicks deep inside one
        // meeting; a summary card that knows its meeting has no minutes is the new door.
        documents.Where(d => d.TranslationRoomId == withMinutes)
            .Should().OnlyContain(d => d.RoomHasMinutes);
        documents.Where(d => d.TranslationRoomId == withoutMinutes)
            .Should().OnlyContain(d => !d.RoomHasMinutes);
    }

    [Fact]
    public async Task Documents_DoNotOfferMinutesForAMeetingNobodySpokeIn()
    {
        var host = Guid.NewGuid();
        var silent = await CreateRoomAsync(host);

        // Exactly what the summary worker writes when there was no speech — and what 161 of
        // production's 275 summaries contain. Minutes drawn from it would carry an attendance list
        // and no proceedings, which is the "I pressed it and nothing came out" report.
        await SeedArtifactAsync(silent, "SUMMARY_EXPORT", "json", InsufficientSummaryJson);
        // Ended, so "there is no finished meeting yet" cannot be the answer and the emptiness of
        // the summary is the only thing left to refuse on.
        await EndRoomAsync(silent);

        var document = (await GetDocumentsAsync(host)).Documents.Single();

        document.CanDraftMinutes.Should().BeFalse(
            "drawing up minutes consumes a number from the workspace's yearly sequence and cannot be undone");
        document.MinutesUnavailableReason.Should().Be(MinutesUnavailableReasons.NothingToRecord);
    }

    [Fact]
    public async Task Documents_SayWhyMinutesAreUnavailable_InTheOrderAReaderWouldAsk()
    {
        var host = Guid.NewGuid();
        var participant = Guid.NewGuid();

        var roomId = await CreateRoomAsync(host);
        await JoinAsync(roomId, participant);
        await SeedArtifactAsync(roomId, "SUMMARY_EXPORT", "json", SummaryJson);

        // A room created through the API is not ENDED, so that answer outranks every other one —
        // there is no meeting to write up yet.
        (await GetDocumentsAsync(host)).Documents.Single()
            .MinutesUnavailableReason.Should().Be(MinutesUnavailableReasons.MeetingNotEnded);

        await EndRoomAsync(roomId);

        var asHost = (await GetDocumentsAsync(host)).Documents.Single();
        asHost.CanDraftMinutes.Should().BeTrue("the meeting ended and its summary has a body");
        asHost.MinutesUnavailableReason.Should().BeNull();

        // Same meeting, same summary — only the chair may draw up its minutes.
        var asParticipant = (await GetDocumentsAsync(participant)).Documents.Single();
        asParticipant.CanDraftMinutes.Should().BeFalse();
        asParticipant.MinutesUnavailableReason.Should().Be(MinutesUnavailableReasons.NotTheChair);

        await SeedMinutesAsync(roomId, host, "BB-2026-0009");

        // Once minutes exist the action is "open", not "draw up" — and CreateDraftAsync is
        // idempotent, so offering it again would be an offer to do nothing.
        (await GetDocumentsAsync(host)).Documents
            .Should().OnlyContain(d => d.MinutesUnavailableReason == MinutesUnavailableReasons.AlreadyDrafted);
    }

    [Fact]
    public async Task Documents_AreScopedToMeetingsTheCallerCanSee()
    {
        var host = Guid.NewGuid();
        var stranger = Guid.NewGuid();

        var roomId = await CreateRoomAsync(host);
        await SeedArtifactAsync(roomId, "SUMMARY_EXPORT", "json", SummaryJson);
        await SeedMinutesAsync(roomId, host, "BB-2026-0002");

        (await GetDocumentsAsync(host)).Total.Should().Be(2);

        // Joined onto the authorized rooms query rather than filtered afterwards, so a meeting the
        // caller cannot open contributes no documents — including its minutes, which are not
        // artifacts and would otherwise have needed their own copy of this rule.
        var asStranger = await GetDocumentsAsync(stranger);
        asStranger.Total.Should().Be(0);
        asStranger.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task Documents_TellAParticipantWhetherTheyMayActuallyOpenTheBody()
    {
        var host = Guid.NewGuid();
        var participant = Guid.NewGuid();

        var roomId = await CreateRoomAsync(host);
        await JoinAsync(roomId, participant);
        await SeedArtifactAsync(roomId, "SUMMARY_EXPORT", "json", SummaryJson);

        (await GetDocumentsAsync(host)).Documents.Should().OnlyContain(d => d.CanOpen && d.IsHost);

        // A room is HOST_ONLY unless somebody says otherwise, so this is the ordinary case, not an
        // edge one. The card must show a lock rather than look available and fail on click.
        var asParticipant = (await GetDocumentsAsync(participant)).Documents;
        asParticipant.Should().ContainSingle("a participant may still see THAT the document exists");
        asParticipant[0].CanOpen.Should().BeFalse("but the download endpoint would refuse them");
        asParticipant[0].IsHost.Should().BeFalse();

        asParticipant.Should().OnlyContain(d => d.MeetingTitle != null && d.TranslationRoomCode != null,
            "the meeting's identity is what makes an unopenable card meaningful");
    }

    [Fact]
    public async Task Documents_FilterByType_AndIgnoreATypeNobodyDefines()
    {
        var host = Guid.NewGuid();
        var roomId = await CreateRoomAsync(host);

        await SeedArtifactAsync(roomId, "TRANSCRIPT_EXPORT", "markdown");
        await SeedArtifactAsync(roomId, "SUMMARY_EXPORT", "json", SummaryJson);
        await SeedMinutesAsync(roomId, host, "BB-2026-0003");

        (await GetDocumentsAsync(host, type: "MINUTES")).Documents
            .Should().ContainSingle().Which.Type.Should().Be("MINUTES");

        var twoTypes = await GetDocumentsAsync(host, type: "TRANSCRIPT_EXPORT,MINUTES");
        twoTypes.Total.Should().Be(2, "a comma-separated filter widens without a second round trip");

        // An unrecognised filter returns NOTHING rather than everything. A typo that silently
        // shows the whole library reads as the filter being broken, which is the harder bug to see.
        (await GetDocumentsAsync(host, type: "NOT_A_TYPE")).Total.Should().Be(0);
    }

    [Fact]
    public async Task Documents_SearchMatchesTheMeeting_NotTheDocument()
    {
        var host = Guid.NewGuid();
        var roomId = await CreateRoomAsync(host);
        await SeedArtifactAsync(roomId, "SUMMARY_EXPORT", "json", SummaryJson);

        var title = await ReadRoomTitleAsync(roomId);

        (await GetDocumentsAsync(host, search: title.ToUpperInvariant()))
            .Total.Should().Be(1, "search is case-insensitive over the meeting's title");

        (await GetDocumentsAsync(host, search: "a phrase no meeting is called"))
            .Total.Should().Be(0);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private async Task<MeetingDocumentsResponse> GetDocumentsAsync(
        Guid userId,
        string? type = null,
        string? search = null,
        int page = 1,
        int pageSize = 24,
        string? email = null)
    {
        var query = $"?page={page}&pageSize={pageSize}";
        if (type is not null) query += $"&type={Uri.EscapeDataString(type)}";
        if (search is not null) query += $"&search={Uri.EscapeDataString(search)}";

        var response = await Client.SendAsync(
            BuildRequest(HttpMethod.Get, $"/api/v1/translation-rooms/documents{query}", userId, email));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MeetingDocumentsResponse>())!;
    }

    private async Task<Guid> CreateRoomAsync(Guid hostId)
    {
        var response = await Client.SendAsync(
            BuildRequest(HttpMethod.Post, "/api/v1/translation-rooms", hostId, body: NewRoomRequest()));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
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

    private static CreateTranslationRoomRequest NewRoomRequest() =>
        new(
            WorkspaceId: Guid.NewGuid(),
            // Unique per room: the search test matches on the title, and a shared one would make
            // "found exactly one" depend on which other tests happened to run first.
            Title: $"Quarterly review {Guid.NewGuid():N}",
            Description: "Seeded by MeetingDocumentsIntegrationTests",
            TranslationRoomType: "INSTANT",
            MaxParticipants: 10,
            SourceLanguage: "vi",
            TargetLanguages: new List<string> { "en" },
            Settings: null,
            ScheduledAt: null,
            InvitedEmails: null);

    private static HttpRequestMessage BuildRequest(
        HttpMethod method, string url, Guid userId, string? email = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId.ToString());
        if (email is not null) request.Headers.Add(TestAuthHandler.EmailHeader, email);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        return request;
    }

    /// <summary>
    /// Seeded directly, because that is how these really arrive: the AI pipeline and the LiveKit
    /// egress webhook write artifacts, and no client-facing endpoint creates one.
    /// </summary>
    private async Task<Guid> SeedArtifactAsync(
        Guid roomId, string artifactType, string fileFormat, string? content = null)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
        var artifact = new TranslationRoomArtifact
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = roomId,
            ArtifactType = artifactType,
            FileFormat = fileFormat,
            Content = content,
            Status = "COMPLETED",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.TranslationRoomArtifacts.Add(artifact);
        await db.SaveChangesAsync();
        return artifact.Id;
    }

    private async Task<Guid> SeedMinutesAsync(Guid roomId, Guid authorId, string minutesNo)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
        var room = await db.TranslationRooms.FindAsync(roomId);

        var minutes = new MeetingMinutes
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = roomId,
            WorkspaceId = room!.WorkspaceId,
            MinutesNo = minutesNo,
            Status = MeetingMinutesConstants.StatusDraft,
            Version = 1,
            IsCurrent = true,
            EditCountVsDraft = 0,
            Content = "{\"attendance\":{\"present\":[],\"absent\":[],\"invitedCount\":0,\"presentCount\":0},\"sections\":[],\"votes\":[]}",
            CreatedAt = DateTime.UtcNow,
            CreatedBy = authorId,
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = authorId
        };
        db.MeetingMinutes.Add(minutes);
        await db.SaveChangesAsync();
        return minutes.Id;
    }

    /// <summary>
    /// Ended in the database rather than through <c>/end</c>: the endpoint drives LiveKit and the
    /// finalizer, neither of which exists here, and the only thing under test is the status.
    /// </summary>
    private async Task EndRoomAsync(Guid roomId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
        var room = await db.TranslationRooms.FindAsync(roomId);
        room!.Status = "ENDED";
        room.EndedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task<string> ReadRoomCodeAsync(Guid roomId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
        var room = await db.TranslationRooms.FindAsync(roomId);
        return room!.TranslationRoomCode;
    }

    private async Task<string> ReadRoomTitleAsync(Guid roomId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
        var room = await db.TranslationRooms.FindAsync(roomId);
        return room!.Title;
    }
}
