using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;
using WarpTalk.Shared.Protos;
using WarpTalk.WorkspaceService.API.GrpcServices;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// The two ways an entry leaves the store and arrives in it that are not the query itself: the
/// gRPC append every other service records through, and the CSV an admin downloads.
/// </summary>
public class AdminAuditTransportTests
{
    [Fact]
    public async Task GrpcAppend_CarriesTheAdminsRequestContextIntoTheStore()
    {
        var auditLog = Substitute.For<IAdminAuditLogService>();
        AdminActionRecordedEvent? recorded = null;
        auditLog.RecordAsync(Arg.Do<AdminActionRecordedEvent>(e => recorded = e), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var service = new AdminAuditGrpcService(auditLog);

        var response = await service.RecordAdminAction(new RecordAdminActionRequest
        {
            SourceService = AdminAuditSources.TranslationRoomService,
            Action = AdminAuditLanguageActions.Disabled,
            EntityType = AdminAuditEntityTypes.SupportedLanguage,
            ActorId = Guid.NewGuid().ToString(),
            Result = AdminAuditResults.Failed,
            ActorEmail = "root@warptalk.io.vn",
            ActorName = "",
            EntityKey = "vi",
            EntityLabel = "Vietnamese",
            ErrorMessage = "At least one language must stay enabled",
            IpAddress = "203.0.113.7",
            UserAgent = "Mozilla/5.0",
        }, Substitute.For<ServerCallContext>());

        Assert.True(response.Recorded);
        Assert.NotNull(recorded);
        Assert.Equal("root@warptalk.io.vn", recorded!.ActorEmail);
        Assert.Null(recorded.ActorName);
        Assert.Equal("vi", recorded.EntityKey);
        Assert.Null(recorded.EntityId);
        Assert.Equal("Vietnamese", recorded.EntityLabel);
        Assert.Equal("At least one language must stay enabled", recorded.ErrorMessage);
        Assert.Equal("203.0.113.7", recorded.IpAddress);
        Assert.Equal("Mozilla/5.0", recorded.UserAgent);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "\"'=HYPERLINK(\"\"http://evil\"\")\"")]
    [InlineData("+1 234", "'+1 234")]
    [InlineData("-5 credits", "'-5 credits")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("Spam, repeated", "\"Spam, repeated\"")]
    [InlineData("line one\nline two", "\"line one\nline two\"")]
    [InlineData("Tạm khoá vì spam", "Tạm khoá vì spam")]
    [InlineData(null, "")]
    public void CsvCell_QuotesAndNeutralisesFormulas(string? input, string expected) =>
        Assert.Equal(expected, AdminAuditCsv.Cell(input));

    [Fact]
    public void CsvRender_WritesAHeaderAndOneRowPerEntryAsUtf8WithBom()
    {
        var entry = new AdminAuditLogEntryDto(
            Guid.NewGuid(),
            new DateTime(2026, 9, 24, 8, 0, 0, DateTimeKind.Utc),
            AdminAuditSources.WorkspaceService,
            "suspend",
            new AdminAuditActorDto(Guid.NewGuid(), "Root", "root@warptalk.io.vn"),
            new AdminAuditEntityDto(AdminAuditEntityTypes.Workspace, Guid.NewGuid(), null, "Acme", null, null, null),
            "Spam, repeated",
            AdminAuditResults.Succeeded,
            null,
            new AdminAuditRequestDto("trace-1", "203.0.113.7", "Mozilla/5.0"),
            new System.Collections.Generic.Dictionary<string, string?> { ["status"] = "active" },
            new System.Collections.Generic.Dictionary<string, string?> { ["status"] = "suspended" });

        var bytes = AdminAuditCsv.Render([entry]);

        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes.Take(3).ToArray());
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(string.Join(',', AdminAuditCsv.Header), lines[0]);
        Assert.Contains("2026-09-24T08:00:00Z,succeeded,suspend", lines[1]);
        Assert.Contains("\"Spam, repeated\"", lines[1]);
        Assert.Contains("\"{\"\"status\"\":\"\"suspended\"\"}\"", lines[1]);
    }
}

/// <summary>
/// This service's own admin endpoints write the store directly; the repository stamps who and from
/// where out of the admin's HTTP request — and must not stamp a service's gRPC call as a person.
/// </summary>
public class AdminAuditRepositoryStampingTests
{
    private static WarpTalk.WorkspaceService.Infrastructure.Persistence.WorkspaceDbContext Context() =>
        new(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<WarpTalk.WorkspaceService.Infrastructure.Persistence.WorkspaceDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options);

    private static Microsoft.AspNetCore.Http.DefaultHttpContext Request(string? contentType = null)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Headers["X-Forwarded-For"] = "198.51.100.4, 10.0.0.2";
        http.Request.Headers.UserAgent = "Mozilla/5.0 (Macintosh)";
        http.Request.ContentType = contentType;
        http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Email, "root@warptalk.io.vn")], "test"));
        return http;
    }

    private static WarpTalk.WorkspaceService.Domain.Entities.WorkspaceAdminAction Entry() => new()
    {
        Id = Guid.NewGuid(),
        SourceService = AdminAuditSources.WorkspaceService,
        Action = "suspend",
        EntityType = AdminAuditEntityTypes.Workspace,
        Reason = "Spam",
        Result = AdminAuditResults.Succeeded,
        PerformedBy = Guid.NewGuid(),
        PerformedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task StampsTheAdminsOwnRequest()
    {
        using var context = Context();
        var accessor = new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = Request() };
        var repository = new WarpTalk.WorkspaceService.Infrastructure.Repositories.AdminAuditLogRepository(context, accessor);
        var entry = Entry();

        await repository.AppendAsync(entry);

        Assert.Equal("root@warptalk.io.vn", entry.ActorEmail);
        Assert.Equal("198.51.100.4", entry.IpAddress);
        Assert.Equal("Mozilla/5.0 (Macintosh)", entry.UserAgent);
    }

    [Fact]
    public async Task StampsNothingOntoAnEntryArrivingOverGrpc()
    {
        using var context = Context();
        var accessor = new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = Request("application/grpc") };
        var repository = new WarpTalk.WorkspaceService.Infrastructure.Repositories.AdminAuditLogRepository(context, accessor);
        var entry = Entry();

        await repository.AppendAsync(entry);

        Assert.Null(entry.ActorEmail);
        Assert.Null(entry.IpAddress);
        Assert.Null(entry.UserAgent);
    }

    [Fact]
    public async Task KeepsWhatTheProducerAlreadySet()
    {
        using var context = Context();
        var accessor = new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = Request() };
        var repository = new WarpTalk.WorkspaceService.Infrastructure.Repositories.AdminAuditLogRepository(context, accessor);
        var entry = Entry();
        entry.IpAddress = "203.0.113.7";

        await repository.AppendAsync(entry);

        Assert.Equal("203.0.113.7", entry.IpAddress);
    }
}
