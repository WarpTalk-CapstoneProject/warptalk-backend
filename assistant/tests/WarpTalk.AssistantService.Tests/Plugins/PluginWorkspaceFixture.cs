using System.Linq.Expressions;
using NSubstitute;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// In-memory tables behind every read <c>PluginWorkspaceMatrix</c> makes, so an admin test states
/// the world ("three workspaces, one member connected Linear in B") instead of stubbing queries.
/// </summary>
/// <remarks>
/// Predicates are compiled and run against the lists, so a query that filters wrongly returns the
/// wrong rows here too, rather than whatever a stub was told to hand back.
/// </remarks>
internal sealed class PluginWorkspaceFixture
{
    public List<PlatformWorkspace> Workspaces { get; } = [];
    public List<WorkspacePluginOverride> Overrides { get; } = [];
    public List<WorkspacePluginCuration> Curations { get; } = [];
    public List<WorkspacePlugin> ListRows { get; } = [];
    public List<WorkspacePluginUsage> Usage { get; } = [];
    public List<PluginInstallation> Installations { get; } = [];
    public List<PluginConnection> Connections { get; } = [];
    public List<(Guid UserId, Guid WorkspaceId)> Memberships { get; } = [];

    /// <summary>The workspace service does not answer.</summary>
    public bool DirectoryDown { get; set; }

    public IWorkspaceDirectoryClient Directory { get; } = Substitute.For<IWorkspaceDirectoryClient>();
    public IWorkspacePluginOverrideRepository OverrideRepository { get; } = Substitute.For<IWorkspacePluginOverrideRepository>();
    public IWorkspacePluginCurationRepository CurationRepository { get; } = Substitute.For<IWorkspacePluginCurationRepository>();

    public PlatformWorkspace AddWorkspace(string name, string? plan = null, bool allowAnyPlugins = true)
    {
        var workspace = new PlatformWorkspace(
            Guid.NewGuid(), name, name.ToLowerInvariant(), "active", Guid.NewGuid(), plan, 3, allowAnyPlugins);
        Workspaces.Add(workspace);
        return workspace;
    }

    /// <summary>A member of <paramref name="workspace"/> who installed and connected <paramref name="plugin"/>.</summary>
    public Guid Connect(Plugin plugin, PlatformWorkspace workspace)
    {
        var userId = Guid.NewGuid();
        Installations.Add(new PluginInstallation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PluginId = plugin.Id,
            Status = PluginConstants.InstallationStatus.Installed,
            InstalledAt = DateTime.UtcNow,
            ConnectedAt = DateTime.UtcNow,
        });
        Connections.Add(new PluginConnection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PluginId = plugin.Id,
            Provider = plugin.Provider,
            Status = PluginConstants.ConnectionStatus.Connected,
        });
        Memberships.Add((userId, workspace.WorkspaceId));
        return userId;
    }

    public WorkspacePluginOverride Override(Plugin plugin, PlatformWorkspace workspace, string state, string? reason = null)
    {
        var row = new WorkspacePluginOverride
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.WorkspaceId,
            PluginId = plugin.Id,
            State = state,
            Reason = reason,
            SetAt = DateTime.UtcNow,
        };
        Overrides.Add(row);
        return row;
    }

    /// <summary>The Owner of <paramref name="workspace"/> curated its list and added <paramref name="plugin"/>.</summary>
    public void AddToList(Plugin plugin, PlatformWorkspace workspace)
    {
        if (Curations.All(c => c.WorkspaceId != workspace.WorkspaceId))
            Curations.Add(new WorkspacePluginCuration { WorkspaceId = workspace.WorkspaceId, CuratedAt = DateTime.UtcNow });
        ListRows.Add(new WorkspacePlugin
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.WorkspaceId,
            PluginId = plugin.Id,
            AddedAt = DateTime.UtcNow,
        });
    }

    public void Wire(
        IUnitOfWork unitOfWork,
        IWorkspacePluginRepository listRepository,
        IPluginToolAuditRepository auditRepository,
        IPluginInstallationRepository installationRepository,
        IPluginConnectionRepository connectionRepository)
    {
        unitOfWork.WorkspacePluginOverrideRepository.Returns(OverrideRepository);
        unitOfWork.WorkspacePluginCurationRepository.Returns(CurationRepository);
        unitOfWork.WorkspacePluginRepository.Returns(listRepository);
        unitOfWork.PluginToolAuditRepository.Returns(auditRepository);
        unitOfWork.PluginInstallationRepository.Returns(installationRepository);
        unitOfWork.PluginConnectionRepository.Returns(connectionRepository);

        StubFind(OverrideRepository, () => Overrides);
        OverrideRepository.AddAsync(Arg.Any<WorkspacePluginOverride>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Overrides.Add(call.Arg<WorkspacePluginOverride>());
                return Task.CompletedTask;
            });
        OverrideRepository.When(r => r.Remove(Arg.Any<WorkspacePluginOverride>()))
            .Do(call => Overrides.Remove(call.Arg<WorkspacePluginOverride>()));
        StubFind(CurationRepository, () => Curations);
        StubFind(listRepository, () => ListRows);
        StubFind(installationRepository, () => Installations);
        StubFind(connectionRepository, () => Connections);

        auditRepository.GetSuccessfulUsageByWorkspaceAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = call.ArgAt<IReadOnlyCollection<Guid>>(0);
                var only = call.ArgAt<Guid?>(1);
                return (IReadOnlyList<WorkspacePluginUsage>)Usage
                    .Where(u => ids.Contains(u.PluginId) && (only == null || u.WorkspaceId == only))
                    .ToList();
            });

        Directory.ListPlatformWorkspacesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => DirectoryDown ? null : (IReadOnlyList<PlatformWorkspace>)Workspaces.ToList());
        Directory.ListActiveMembershipsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (DirectoryDown) return null;
                var users = call.ArgAt<IReadOnlyCollection<Guid>>(0);
                return (IReadOnlyList<(Guid, Guid)>)Memberships.Where(m => users.Contains(m.UserId)).ToList();
            });
        Directory.ListActiveMemberUserIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (DirectoryDown) return null;
                var workspaceId = call.ArgAt<Guid>(0);
                return (IReadOnlyList<Guid>)Memberships.Where(m => m.WorkspaceId == workspaceId).Select(m => m.UserId).ToList();
            });
    }

    private static void StubFind<T>(IGenericRepository<T> repository, Func<List<T>> rows) where T : class =>
        repository.FindAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<T>)rows()
                .Where(call.Arg<Expression<Func<T, bool>>>().Compile())
                .ToList());
}
