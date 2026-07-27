using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.Shared.Extensions;
using WarpTalk.WorkspaceService.API.Providers;
using WarpTalk.WorkspaceService.Application.Evaluators;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddScoped<IWorkspaceDocumentService, WarpTalk.WorkspaceService.Application.Services.WorkspaceDocumentService>();
builder.Services.AddScoped<IVerifiedDomainService, WarpTalk.WorkspaceService.Application.Services.VerifiedDomainService>();
builder.Services.AddScoped<IDocumentAccessEvaluator, DocumentAccessEvaluator>();

// --- Infrastructure Layer Services (DbContext, Repositories, Storage, Redis, gRPC Clients, Consumers) ---
builder.Services.AddInfrastructureServices(builder.Configuration);

// --- Cross-Cutting & Core Extensions ---
builder.Services.AddResendClient(builder.Configuration);
builder.Services.AddWarpTalkMassTransit(builder.Configuration);
builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IWorkspaceUrlProvider, WorkspaceUrlProvider>();

// --- Authentication & Framework Services ---
builder.Services.AddWarpTalkJwtAuthentication(builder.Configuration);
builder.Services.AddAuthorization();
builder.Services.AddGrpc();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGrpcService<WarpTalk.WorkspaceService.API.GrpcServices.WorkspaceInvitationGrpcService>();
app.MapGrpcService<WarpTalk.WorkspaceService.API.GrpcServices.WorkspaceGrpcService>();

app.MapGet("/", () => "WarpTalk Workspace Service is running.");

app.Run();

public partial class Program { }
