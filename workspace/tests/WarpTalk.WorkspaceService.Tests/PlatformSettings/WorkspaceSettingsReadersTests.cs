using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.Shared.PlatformSettings;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.Evaluators;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Interfaces.Caching;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;
using Xunit;
using AppWorkspaceService = WarpTalk.WorkspaceService.Application.Services.WorkspaceService;

namespace WarpTalk.WorkspaceService.Tests.PlatformSettings;

/// <summary>
/// The workspace service's own readers use the LIVE value: the knowledge upload limit (with its
/// plan and workspace overrides) and the new-workspace language and time zone. Each test sets a
/// value, drives the real code path, changes the value and drives it again.
/// </summary>
public sealed class WorkspaceSettingsReadersTests
{
    private readonly InMemoryPlatformSettingsSource _source = new();
    private readonly PlatformSettingsReader _reader;

    public WorkspaceSettingsReadersTests()
    {
        _reader = new PlatformSettingsReader(_source, NullLogger<PlatformSettingsReader>.Instance, cacheTtl: TimeSpan.Zero);
    }

    [Fact]
    public async Task Upload_limit_follows_the_setting_and_its_plan_and_workspace_overrides()
    {
        var workspaceId = Guid.NewGuid();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var snapshots = Substitute.For<IWorkspaceEntitlementSnapshotRepository>();
        unitOfWork.WorkspaceEntitlementSnapshotRepository.Returns(snapshots);
        snapshots.GetForWorkspaceAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceEntitlementSnapshot { WorkspaceId = workspaceId, PlanSlug = "pro" });
        var service = DocumentService(unitOfWork, _reader);
        const long twelveMb = 12L * 1024 * 1024;

        // Nothing set: the 10 MB the endpoint always had.
        Assert.NotNull(await service.UploadTooLargeAsync(workspaceId, twelveMb));

        _source.Set(PlatformSettingsCatalog.DocumentUploadMb, 20);
        Assert.Null(await service.UploadTooLargeAsync(workspaceId, twelveMb));

        _source.Set(PlatformSettingsCatalog.DocumentUploadMb, 11, PlatformSettingsRedisKeys.PlanHash("pro"));
        Assert.Contains("11 MB", await service.UploadTooLargeAsync(workspaceId, twelveMb));

        _source.Set(PlatformSettingsCatalog.DocumentUploadMb, 60, PlatformSettingsRedisKeys.WorkspaceHash(workspaceId));
        Assert.Null(await service.UploadTooLargeAsync(workspaceId, 59L * 1024 * 1024));
        Assert.NotNull(await service.UploadTooLargeAsync(workspaceId, 61L * 1024 * 1024));
    }

    [Fact]
    public async Task New_workspaces_start_with_the_live_language_and_time_zone()
    {
        var userId = Guid.NewGuid();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var workspaces = Substitute.For<IWorkspaceRepository>();
        var domains = Substitute.For<IWorkspaceVerifiedDomainRepository>();
        unitOfWork.WorkspaceRepository.Returns(workspaces);
        unitOfWork.WorkspaceMemberRepository.Returns(Substitute.For<IWorkspaceMemberRepository>());
        unitOfWork.WorkspaceVerifiedDomainRepository.Returns(domains);
        domains.FindAsync(Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceVerifiedDomain>());
        var identity = Substitute.For<IAuthIdentityClient>();
        identity.GetUserByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(new User { Id = userId, Email = "owner@acme.example" });
        identity.GetRoleByNameAsync("Owner", Arg.Any<CancellationToken>()).Returns(new Role { Id = Guid.NewGuid(), Name = "Owner" });
        var created = new List<Workspace>();
        _ = workspaces.AddAsync(Arg.Do<Workspace>(created.Add), Arg.Any<CancellationToken>());

        var service = new AppWorkspaceService(
            unitOfWork, Substitute.For<IWorkspaceCacheService>(), Substitute.For<ILogger<AppWorkspaceService>>(), identity,
            Substitute.For<IWorkspaceEventPublisher>(), platformSettings: _reader);

        Assert.True((await service.CreateWorkspaceAsync(new CreateWorkspaceRequest("Before", null), userId)).IsSuccess);
        var before = Config(created[^1]);
        Assert.Equal("en", before.DefaultLanguage);
        Assert.Equal("UTC", before.Timezone);

        _source.Set(PlatformSettingsCatalog.WorkspaceDefaultLanguage, "vi")
               .Set(PlatformSettingsCatalog.WorkspaceDefaultTimezone, "Asia/Ho_Chi_Minh");
        Assert.True((await service.CreateWorkspaceAsync(new CreateWorkspaceRequest("After", null), userId)).IsSuccess);
        var after = Config(created[^1]);
        Assert.Equal("vi", after.DefaultLanguage);
        Assert.Equal("Asia/Ho_Chi_Minh", after.Timezone);
    }

    private static WorkspaceConfiguration Config(Workspace workspace)
        => JsonSerializer.Deserialize<WorkspaceConfiguration>(workspace.Settings!)!;

    private static WorkspaceDocumentService DocumentService(IUnitOfWork unitOfWork, IPlatformSettings settings)
        => new(
            unitOfWork,
            Substitute.For<IDocumentAccessEvaluator>(),
            Substitute.For<IWorkspaceDocumentEventPublisher>(),
            Substitute.For<IAuthIdentityClient>(),
            Substitute.For<IWorkspaceUrlProvider>(),
            Substitute.For<ITranslationRoomClient>(),
            Substitute.For<IWorkspaceDocumentStorage>(),
            Substitute.For<IDocumentTextExtractor>(),
            Substitute.For<IKnowledgeChunkWriter>(),
            NullLogger<WorkspaceDocumentService>.Instance,
            settings);
}
