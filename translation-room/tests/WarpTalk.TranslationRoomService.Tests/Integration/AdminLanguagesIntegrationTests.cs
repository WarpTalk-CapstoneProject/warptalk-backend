using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;
using WarpTalk.TranslationRoomService.Application.DTOs.Admin;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Integration;

/// <summary>
/// WT-691: the admin language catalog can be managed — add, rename, enable, soft-disable — against
/// real PostgreSQL and the real policy, with every change recorded in the audit log first and
/// abandoned when that record fails. The catalog is seeded by the harness in production's
/// locale-tagged shape (en-US, vi-VN, ja-JP, ko-KR, zh-CN, fr-FR, es-ES).
/// </summary>
public sealed class AdminLanguagesIntegrationTests : BaseIntegrationTest
{
    private const string Url = "/api/v1/admin/languages";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly RecordingAuditRecorder _audit = new();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        var existing = services.Where(d => d.ServiceType == typeof(IAdminAuditRecorder)).ToList();
        foreach (var descriptor in existing) services.Remove(descriptor);
        services.AddSingleton<IAdminAuditRecorder>(_audit);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, object? body = null, string? roles = "admin")
    {
        var request = new HttpRequestMessage(method, url);
        if (roles is not null) request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task SeedRoomsAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
        db.TranslationRooms.AddRange(
            // Live: Vietnamese source, English target.
            Room("IN_PROGRESS", "vi", "[\"en\"]"),
            // Booked: Japanese source, Korean target.
            Room("SCHEDULED", "ja", "[\"ko\"]"),
            // Finished: Chinese. Must not count as in use.
            Room("ENDED", "zh", "[\"fr\"]"));
        await db.SaveChangesAsync();
    }

    private static TranslationRoom Room(string status, string source, string targets)
    {
        var id = Guid.NewGuid();
        var at = new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc);
        return new TranslationRoom
        {
            Id = id,
            WorkspaceId = Guid.NewGuid(),
            HostId = Guid.NewGuid(),
            Title = $"Room {status}",
            TranslationRoomCode = $"WARP-{id.ToString("N")[..6]}",
            Status = status,
            TranslationRoomType = "STANDARD",
            MaxParticipants = 10,
            SourceLanguage = source,
            TargetLanguages = targets,
            Settings = "{}",
            IsActive = true,
            CreatedAt = at,
            UpdatedAt = at,
        };
    }

    private async Task<SupportedLanguage?> ReadRowAsync(string code)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
        return await db.SupportedLanguages.AsNoTracking().SingleOrDefaultAsync(l => l.Code == code);
    }

    private async Task<List<AdminLanguageDto>> ListAsync()
    {
        var response = await Client.SendAsync(Request(HttpMethod.Get, Url));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<AdminLanguageDto>>(Json))!;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Member")]
    public async Task Every_endpoint_refuses_non_admins(string? roles)
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.SendAsync(Request(HttpMethod.Get, Url, roles: roles))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Client.SendAsync(Request(HttpMethod.Post, Url, new { code = "de", name = "German" }, roles))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Client.SendAsync(Request(HttpMethod.Post, Url + "/fr-FR/disable", new { }, roles))).StatusCode);
        Assert.Empty(_audit.Calls);
    }

    [Fact]
    public async Task The_listing_counts_live_and_scheduled_meetings_per_language()
    {
        await SeedRoomsAsync();

        var catalog = await ListAsync();

        Assert.Equal(7, catalog.Count);
        var vi = catalog.Single(l => l.Code == "vi-VN");
        Assert.Equal((1, 0), (vi.LiveMeetings, vi.UpcomingMeetings));
        var en = catalog.Single(l => l.Code == "en-US");
        Assert.Equal((1, 0), (en.LiveMeetings, en.UpcomingMeetings));
        var ja = catalog.Single(l => l.Code == "ja-JP");
        Assert.Equal((0, 1), (ja.LiveMeetings, ja.UpcomingMeetings));
        var ko = catalog.Single(l => l.Code == "ko-KR");
        Assert.Equal((0, 1), (ko.LiveMeetings, ko.UpcomingMeetings));
        // An ENDED meeting in Chinese / French is history, not use.
        var zh = catalog.Single(l => l.Code == "zh-CN");
        Assert.Equal((0, 0), (zh.LiveMeetings, zh.UpcomingMeetings));
    }

    [Fact]
    public async Task Adding_a_language_normalises_the_code_saves_it_and_audits_it()
    {
        var response = await Client.SendAsync(Request(
            HttpMethod.Post, Url, new { code = "de_de", name = " German ", nativeName = "Deutsch" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AdminLanguageDto>(Json);
        Assert.Equal("de-DE", created!.Code);
        Assert.Equal("German", created.Name);
        Assert.True(created.IsActive);

        var row = await ReadRowAsync("de-DE");
        Assert.NotNull(row);
        Assert.Equal("Deutsch", row!.NativeName);

        var call = Assert.Single(_audit.Calls);
        Assert.Equal(AdminAuditLanguageActions.Created, call.Action);
        Assert.Equal(AdminAuditEntityTypes.SupportedLanguage, call.EntityType);
        Assert.Equal("de-DE", call.After!["code"]);
    }

    [Theory]
    [InlineData("en-US")] // the same row
    [InlineData("EN-us")] // the same row, other casing
    [InlineData("en-GB")] // the same language to room validation, which matches on "en"
    [InlineData("en")]
    public async Task A_language_already_in_the_catalog_is_a_conflict(string code)
    {
        var response = await Client.SendAsync(Request(HttpMethod.Post, Url, new { code, name = "English" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(_audit.Calls);
    }

    [Theory]
    [InlineData("english", "English")]
    [InlineData("en-Latn-US", "English")]
    [InlineData("", "English")]
    [InlineData("de", "")]
    public async Task A_malformed_code_or_blank_name_is_a_400(string code, string name)
    {
        var response = await Client.SendAsync(Request(HttpMethod.Post, Url, new { code, name }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_audit.Calls);
    }

    [Fact]
    public async Task Renaming_updates_the_row_and_audits_before_and_after()
    {
        var response = await Client.SendAsync(Request(
            HttpMethod.Put, Url + "/ko-kr", new { name = "Korean (South Korea)", nativeName = "한국어" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Korean (South Korea)", (await ReadRowAsync("ko-KR"))!.Name);
        var call = Assert.Single(_audit.Calls);
        Assert.Equal(AdminAuditLanguageActions.Updated, call.Action);
        Assert.Equal("Korean", call.Before!["name"]);
        Assert.Equal("Korean (South Korea)", call.After!["name"]);
    }

    [Fact]
    public async Task An_unknown_code_is_a_404()
    {
        var response = await Client.SendAsync(Request(HttpMethod.Put, Url + "/xx-XX", new { name = "Nothing" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_language_a_live_meeting_uses_cannot_be_disabled()
    {
        await SeedRoomsAsync();

        var response = await Client.SendAsync(Request(
            HttpMethod.Post, Url + "/vi-VN/disable", new { confirmUpcoming = true }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True((await ReadRowAsync("vi-VN"))!.IsActive);
        Assert.Empty(_audit.Calls);
    }

    [Fact]
    public async Task A_language_scheduled_meetings_use_needs_confirmation_to_disable()
    {
        await SeedRoomsAsync();

        var refused = await Client.SendAsync(Request(HttpMethod.Post, Url + "/ja-JP/disable", new { }));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.True((await ReadRowAsync("ja-JP"))!.IsActive);

        var confirmed = await Client.SendAsync(Request(
            HttpMethod.Post, Url + "/ja-JP/disable", new { confirmUpcoming = true }));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.False((await ReadRowAsync("ja-JP"))!.IsActive);
        Assert.Equal(AdminAuditLanguageActions.Disabled, Assert.Single(_audit.Calls).Action);
    }

    [Fact]
    public async Task An_unused_language_is_soft_disabled_and_can_be_enabled_again()
    {
        await SeedRoomsAsync();

        var disabled = await Client.SendAsync(Request(HttpMethod.Post, Url + "/es-ES/disable", new { }));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        // Soft: the row is still there, switched off.
        Assert.False((await ReadRowAsync("es-ES"))!.IsActive);
        Assert.Contains(await ListAsync(), l => l.Code == "es-ES" && !l.IsActive);

        var enabled = await Client.SendAsync(Request(HttpMethod.Post, Url + "/es-ES/enable"));
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.True((await ReadRowAsync("es-ES"))!.IsActive);

        Assert.Equal(
            new[] { AdminAuditLanguageActions.Disabled, AdminAuditLanguageActions.Enabled },
            _audit.Calls.Select(c => c.Action).ToArray());
    }

    [Fact]
    public async Task A_change_the_audit_log_refuses_is_not_made()
    {
        _audit.Fail = true;

        var response = await Client.SendAsync(Request(HttpMethod.Post, Url + "/fr-FR/disable", new { }));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True((await ReadRowAsync("fr-FR"))!.IsActive);

        var create = await Client.SendAsync(Request(HttpMethod.Post, Url, new { code = "de", name = "German" }));
        Assert.Equal(HttpStatusCode.InternalServerError, create.StatusCode);
        Assert.Null(await ReadRowAsync("de"));
    }

    private sealed record AuditCall(
        string Action,
        string EntityType,
        IReadOnlyDictionary<string, string?>? Before,
        IReadOnlyDictionary<string, string?>? After);

    private sealed class RecordingAuditRecorder : IAdminAuditRecorder
    {
        private readonly ConcurrentQueue<AuditCall> _calls = new();

        public bool Fail { get; set; }

        public IReadOnlyList<AuditCall> Calls => _calls.ToList();

        public Task<Result> RecordAsync(
            string action,
            string entityType,
            Guid actorId,
            string reason,
            string correlationId,
            IReadOnlyDictionary<string, string?>? beforeSummary = null,
            IReadOnlyDictionary<string, string?>? afterSummary = null,
            CancellationToken ct = default)
        {
            if (Fail)
            {
                return Task.FromResult(Result.Failure("audit log unreachable", ErrorCodes.InternalServerError));
            }

            _calls.Enqueue(new AuditCall(action, entityType, beforeSummary, afterSummary));
            return Task.FromResult(Result.Success());
        }
    }
}
