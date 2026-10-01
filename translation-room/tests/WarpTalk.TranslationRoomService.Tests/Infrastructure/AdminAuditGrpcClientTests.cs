using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using WarpTalk.Shared.Events;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Infrastructure.Clients;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure;

/// <summary>
/// A language-catalog entry reaches the platform audit log naming its language — code as the key,
/// name as the label — and the admin who made it, from the admin's own request.
/// </summary>
public class AdminAuditGrpcClientTests
{
    private sealed class CapturingClient : AdminAuditService.AdminAuditServiceClient
    {
        public RecordAdminActionRequest? Sent { get; private set; }

        public override AsyncUnaryCall<RecordAdminActionResponse> RecordAdminActionAsync(
            RecordAdminActionRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            Sent = request;
            return new AsyncUnaryCall<RecordAdminActionResponse>(
                Task.FromResult(new RecordAdminActionResponse { Recorded = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }
    }

    [Fact]
    public async Task A_language_entry_carries_its_code_its_name_and_the_admins_request()
    {
        var grpc = new CapturingClient();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, "root@warptalk.io.vn")], "test")),
        };
        http.Request.Headers["X-Forwarded-For"] = "198.51.100.4, 10.0.0.9";
        http.Request.Headers.UserAgent = "Mozilla/5.0";
        var client = new AdminAuditGrpcClient(
            grpc, NullLogger<AdminAuditGrpcClient>.Instance, httpContextAccessor: new HttpContextAccessor { HttpContext = http });

        var result = await client.RecordAsync(
            AdminAuditLanguageActions.Disabled,
            AdminAuditEntityTypes.SupportedLanguage,
            Guid.NewGuid(),
            "Disabled vi for new rooms",
            "trace-1",
            new Dictionary<string, string?> { ["code"] = "vi", ["name"] = "Vietnamese", ["is_active"] = "true" },
            new Dictionary<string, string?> { ["code"] = "vi", ["name"] = "Vietnamese", ["is_active"] = "false" });

        Assert.True(result.IsSuccess);
        Assert.Equal("vi", grpc.Sent!.EntityKey);
        Assert.Equal("Vietnamese", grpc.Sent.EntityLabel);
        Assert.Equal(string.Empty, grpc.Sent.EntityId);
        Assert.Equal("root@warptalk.io.vn", grpc.Sent.ActorEmail);
        Assert.Equal("198.51.100.4", grpc.Sent.IpAddress);
        Assert.Equal("Mozilla/5.0", grpc.Sent.UserAgent);
    }
}
