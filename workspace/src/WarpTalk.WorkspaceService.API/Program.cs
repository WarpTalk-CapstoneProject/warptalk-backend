using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Grpc;
using WarpTalk.WorkspaceService.API.Consumers;
using WarpTalk.WorkspaceService.API.Providers;
using WarpTalk.WorkspaceService.Application.Evaluators;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.RequirePublicBaseUrl(builder.Environment, "AppBaseUrl");
builder.Services.AddWarpTalkObservability(
    builder.Configuration,
    builder.Environment,
    "warptalk-workspace");

// Kestrel Ports Configuration
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5106, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
    options.ListenAnyIP(50056, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});

// --- Application Layer Services (Kept in Program.cs for direct Use-Case visibility) ---
builder.Services.AddScoped<IWorkspaceService, WarpTalk.WorkspaceService.Application.Services.WorkspaceService>();
builder.Services.AddScoped<IWorkspaceMemberService, WarpTalk.WorkspaceService.Application.Services.WorkspaceMemberService>();
builder.Services.AddScoped<IWorkspaceInvitationService, WarpTalk.WorkspaceService.Application.Services.WorkspaceInvitationService>();
builder.Services.AddScoped<IWorkspaceInvitationAcceptanceProcessor, WarpTalk.WorkspaceService.Application.Services.WorkspaceInvitationAcceptanceProcessor>();
builder.Services.AddScoped<IWorkspaceDocumentService, WarpTalk.WorkspaceService.Application.Services.WorkspaceDocumentService>();
builder.Services.AddScoped<IVerifiedDomainService, WarpTalk.WorkspaceService.Application.Services.VerifiedDomainService>();
builder.Services.AddScoped<IDocumentAccessEvaluator, DocumentAccessEvaluator>();
builder.Services.AddScoped<IAdminWorkspaceService, WarpTalk.WorkspaceService.Application.Services.AdminWorkspaceService>();
builder.Services.AddScoped<IAdminAuditLogService, WarpTalk.WorkspaceService.Application.Services.AdminAuditLogService>();
builder.Services.AddScoped<IWorkspaceDirectoryService, WarpTalk.WorkspaceService.Application.Services.WorkspaceDirectoryService>();
// WT-335: backs the presence query's membership intersection in the Gateway.
builder.Services.AddScoped<IWorkspaceCoMembershipService, WarpTalk.WorkspaceService.Application.Services.WorkspaceCoMembershipService>();
// Shows an Owner/Admin what the system has indexed about their workspace. Read-only; the
// vector store it reads from is reached through IKnowledgeChunkReader in Infrastructure.
builder.Services.AddScoped<IWorkspaceKnowledgeService, WarpTalk.WorkspaceService.Application.Services.WorkspaceKnowledgeService>();

// --- Infrastructure Layer Services (DbContext, Repositories, Storage, Redis, gRPC Clients, Consumers) ---
builder.Services.AddInfrastructureServices(builder.Configuration, builder.Environment);
builder.Services.AddWarpTalkServiceHealthChecks<
    WarpTalk.WorkspaceService.Infrastructure.Persistence.WorkspaceDbContext>(
    "workspace-database");

// --- Cross-Cutting & Core Extensions ---
builder.Services.AddResendClient(builder.Configuration, builder.Environment);
// Consumes admin.action_recorded from the other services: they own separate logical
// databases, so the bus is how their admin actions reach the audit store here (WT-210).
builder.Services.AddWarpTalkMassTransit(
    builder.Configuration,
    registration => registration.AddConsumer<AdminActionRecordedConsumer>());
builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IWorkspaceUrlProvider, WorkspaceUrlProvider>();

// --- Authentication & Framework Services ---
builder.Services.AddWarpTalkJwtAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAuthorization();
builder.Services.AddWarpTalkSystemAdminAuthorization();
builder.Services.AddWarpTalkGrpcServer(builder.Configuration, builder.Environment);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGrpcService<WarpTalk.WorkspaceService.API.GrpcServices.WorkspaceInvitationGrpcService>();
app.MapGrpcService<WarpTalk.WorkspaceService.API.GrpcServices.WorkspaceGrpcService>();
app.MapWarpTalkServiceHealthChecks();

app.MapGet("/", () => "WarpTalk Workspace Service is running.");

app.Run();

public partial class Program { }
