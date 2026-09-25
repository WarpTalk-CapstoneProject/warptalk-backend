using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Tests.Conversations;

/// <summary>
/// Platform and workspace conversations are stored apart, and neither scope can reach the other.
///
/// One user holds both kinds here — a system admin is usually also a member of some workspace —
/// which is exactly the case where a shared store would leak: the same user id owns rows in both.
/// </summary>
public class PlatformConversationScopeIsolationTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _workspaceId = Guid.NewGuid();

    private readonly List<AssistantConversation> _workspaceConversations = [];
    private readonly List<AssistantMessage> _workspaceMessages = [];
    private readonly List<PlatformConversation> _platformConversations = [];
    private readonly List<PlatformMessage> _platformMessages = [];

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAssistantChatRequestPublisher _publisher = Substitute.For<IAssistantChatRequestPublisher>();
    private readonly AssistantConversationService _workspaceService;
    private readonly PlatformAssistantConversationService _platformService;

    public PlatformConversationScopeIsolationTests()
    {
        // Built into locals first: configuring a substitute inside another's Returns() confuses
        // NSubstitute's "last call" tracking.
        var workspaceConversations = Repository<IAssistantConversationRepository, AssistantConversation>(
            _workspaceConversations, c => c.Id, (c, name) => name == "Messages"
                ? c.Messages = _workspaceMessages.Where(m => m.ConversationId == c.Id).ToList()
                : null);
        var workspaceMessages = Repository<IAssistantMessageRepository, AssistantMessage>(
            _workspaceMessages, m => m.Id, (_, _) => null);
        var platformConversations = Repository<IPlatformConversationRepository, PlatformConversation>(
            _platformConversations, c => c.Id, (c, name) => name == "Messages"
                ? c.Messages = _platformMessages.Where(m => m.ConversationId == c.Id).ToList()
                : null);
        var platformMessages = Repository<IPlatformMessageRepository, PlatformMessage>(
            _platformMessages, m => m.Id, (_, _) => null);
        _unitOfWork.AssistantConversationRepository.Returns(workspaceConversations);
        _unitOfWork.AssistantMessageRepository.Returns(workspaceMessages);
        _unitOfWork.PlatformConversationRepository.Returns(platformConversations);
        _unitOfWork.PlatformMessageRepository.Returns(platformMessages);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);

        _workspaceService = new AssistantConversationService(_unitOfWork, _publisher);
        _platformService = new PlatformAssistantConversationService(_unitOfWork, _publisher);
    }

    /// <summary>A list-backed repository that evaluates the service's own predicates.</summary>
    private static TRepo Repository<TRepo, T>(List<T> rows, Func<T, Guid> id, Func<T, string, object?> include)
        where TRepo : class, IGenericRepository<T>
        where T : class
    {
        var repo = Substitute.For<TRepo>();
        repo.AddAsync(Arg.Do<T>(rows.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => rows.SingleOrDefault(row => id(row) == call.Arg<Guid>()));
        repo.FindAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<T>>(
                rows.Where(call.Arg<Expression<Func<T, bool>>>().Compile()).ToList()));
        repo.FirstOrDefaultAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var row = rows.FirstOrDefault(call.Arg<Expression<Func<T, bool>>>().Compile());
                if (row != null && !string.IsNullOrEmpty(call.Arg<string>())) include(row, call.Arg<string>());
                return Task.FromResult(row);
            });
        return repo;
    }

    private async Task<(Guid WorkspaceConversation, Guid PlatformConversation)> OneOfEachAsync()
    {
        var workspace = await _workspaceService.CreateConversationAsync(
            _userId, new CreateAssistantConversationRequest(_workspaceId));
        var platform = await _platformService.CreateConversationAsync(_userId, new CreatePlatformConversationRequest());
        Assert.True(workspace.IsSuccess && platform.IsSuccess);

        await _workspaceService.SendMessageAsync(
            workspace.Value!.Id, _userId, "Bearer t", new SendAssistantMessageRequest("What did the Acme meeting decide?"));
        await _platformService.SendMessageAsync(
            platform.Value!.Id, _userId, "Bearer t", new SendPlatformMessageRequest("Revenue this month vs last"));
        return (workspace.Value.Id, platform.Value.Id);
    }

    [Fact]
    public async Task EachScopeIsWrittenOnlyToItsOwnStore()
    {
        var (workspaceId, platformId) = await OneOfEachAsync();

        Assert.Equal([workspaceId], _workspaceConversations.Select(c => c.Id));
        Assert.Equal([platformId], _platformConversations.Select(c => c.Id));
        Assert.All(_workspaceMessages, m => Assert.Equal(workspaceId, m.ConversationId));
        Assert.All(_platformMessages, m => Assert.Equal(platformId, m.ConversationId));
    }

    [Fact]
    public async Task NeitherListShowsTheOtherScope()
    {
        var (workspaceId, platformId) = await OneOfEachAsync();

        var workspaceList = (await _workspaceService.ListConversationsAsync(_workspaceId, _userId)).Value!.ToList();
        var platformList = (await _platformService.ListConversationsAsync(_userId)).Value!.ToList();

        Assert.Equal([workspaceId], workspaceList.Select(c => c.Id));
        Assert.Equal([platformId], platformList.Select(c => c.Id));
        Assert.All(workspaceList, c => Assert.Equal(AssistantConversationScopes.Workspace, c.Scope));
        Assert.All(platformList, c => Assert.Equal(AssistantConversationScopes.Platform, c.Scope));

        // The sentinel trap: an unset workspace id is Guid.Empty, and it must not list platform rows.
        var emptyWorkspace = (await _workspaceService.ListConversationsAsync(Guid.Empty, _userId)).Value!;
        Assert.Empty(emptyWorkspace);
    }

    [Fact]
    public async Task NeitherScopeCanOpenOrSendIntoTheOthersConversation()
    {
        var (workspaceId, platformId) = await OneOfEachAsync();

        Assert.Equal(ErrorCodes.NotFound, (await _workspaceService.GetConversationAsync(platformId, _userId)).ErrorCode);
        Assert.Equal(ErrorCodes.NotFound, (await _platformService.GetConversationAsync(workspaceId, _userId)).ErrorCode);
        Assert.False((await _workspaceService.AuthorizeConversationAccessAsync(platformId, _userId)).IsSuccess);
        Assert.False((await _platformService.AuthorizeConversationAccessAsync(workspaceId, _userId)).IsSuccess);

        var intoPlatform = await _workspaceService.SendMessageAsync(
            platformId, _userId, "Bearer t", new SendAssistantMessageRequest("hi"));
        var intoWorkspace = await _platformService.SendMessageAsync(
            workspaceId, _userId, "Bearer t", new SendPlatformMessageRequest("hi"));
        Assert.Equal(ErrorCodes.NotFound, intoPlatform.ErrorCode);
        Assert.Equal(ErrorCodes.NotFound, intoWorkspace.ErrorCode);
    }

    [Fact]
    public async Task APlatformTurn_ReachesTheWorkerWithNoWorkspaceAndOnlyItsOwnHistory()
    {
        var (_, platformId) = await OneOfEachAsync();

        var platformCall = _publisher.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IAssistantChatRequestPublisher.PublishPlatformAsync));
        var args = platformCall.GetArguments();
        Assert.Equal(platformId, (Guid)args[1]!);
        var history = (IReadOnlyList<ChatTurnDto>)args[4]!;
        Assert.Equal(["Revenue this month vs last"], history.Select(turn => turn.Content));

        // And the workspace turn went out on the workspace path, never the platform one.
        var workspaceCall = _publisher.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IAssistantChatRequestPublisher.PublishAsync));
        Assert.Equal(_workspaceId, (Guid)workspaceCall.GetArguments()[2]!);
        var workspaceHistory = (IReadOnlyList<ChatTurnDto>)workspaceCall.GetArguments()[5]!;
        Assert.DoesNotContain(workspaceHistory, turn => turn.Content.Contains("Revenue"));
    }

    [Fact]
    public void ThePlatformTables_HaveNoWorkspaceColumn()
    {
        using var db = new AssistantDbContext(
            new DbContextOptionsBuilder<AssistantDbContext>().UseNpgsql("Host=unused").Options);

        foreach (var clr in new[] { typeof(PlatformConversation), typeof(PlatformMessage) })
        {
            var entity = db.Model.FindEntityType(clr)!;
            Assert.Equal("assistant", entity.GetSchema());
            Assert.StartsWith("platform_", entity.GetTableName());
            var columns = entity.GetProperties().Select(p => p.GetColumnName()).ToList();
            Assert.DoesNotContain("workspace_id", columns);
            // Hand-mapped like the rest of this context: every column must be snake_case, or EF
            // sends the PascalCase name and every SELECT over the table fails.
            Assert.All(columns, column => Assert.Matches("^[a-z0-9_]+$", column));
        }

        Assert.NotEqual(
            db.Model.FindEntityType(typeof(AssistantConversation))!.GetTableName(),
            db.Model.FindEntityType(typeof(PlatformConversation))!.GetTableName());
    }
}
