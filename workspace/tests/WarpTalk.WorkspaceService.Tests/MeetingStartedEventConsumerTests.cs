using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class MeetingStartedEventConsumerTests
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly IServiceProvider _serviceProvider;
    private readonly IServiceScope _serviceScope;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IGenericRepository<WorkspaceDocument> _workspaceDocumentRepository;
    private readonly IWorkspaceDocumentStorage _storage;
    private readonly MeetingStartedEventConsumer _service;

    public MeetingStartedEventConsumerTests()
    {
        _redis = Substitute.For<IConnectionMultiplexer>();
        _db = Substitute.For<IDatabase>();
        _redis.GetDatabase().Returns(_db);

        _serviceProvider = Substitute.For<IServiceProvider>();
        _serviceScope = Substitute.For<IServiceScope>();
        var serviceScopeFactory = Substitute.For<IServiceScopeFactory>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _workspaceDocumentRepository = Substitute.For<IGenericRepository<WorkspaceDocument>>();
        _storage = Substitute.For<IWorkspaceDocumentStorage>();

        _serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(serviceScopeFactory);
        serviceScopeFactory.CreateScope().Returns(_serviceScope);
        _serviceScope.ServiceProvider.Returns(_serviceProvider);
        _serviceProvider.GetService(typeof(IUnitOfWork)).Returns(_unitOfWork);
        _serviceProvider.GetService(typeof(IWorkspaceDocumentStorage)).Returns(_storage);
        _unitOfWork.WorkspaceDocumentRepository.Returns(_workspaceDocumentRepository);

        _service = new MeetingStartedEventConsumer(
            _redis,
            _serviceProvider,
            Substitute.For<ILogger<MeetingStartedEventConsumer>>()
        );
    }

    [Fact]
    public async Task ProcessContextSnapshotAsync_ShouldNotSetRedis_WhenNoAiEligibleDocuments()
    {
        // Arrange
        var roomId = "room-123";
        var workspaceId = Guid.NewGuid();
        
        _workspaceDocumentRepository.FindAsync(default!, default!, default!)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<WorkspaceDocument>>(new List<WorkspaceDocument>()));

        // Act
        await _service.ProcessContextSnapshotAsync(roomId, workspaceId, CancellationToken.None);

        // Assert
        await _db.DidNotReceive().StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task ProcessContextSnapshotAsync_ShouldCreateSnapshotAndSetRedis_WhenDocumentsExist()
    {
        // Arrange
        var roomId = "room-123";
        var workspaceId = Guid.NewGuid();
        var doc1 = new WorkspaceDocument { Id = Guid.NewGuid(), WorkspaceId = workspaceId, FileName = "doc1.txt" };
        var doc2 = new WorkspaceDocument { Id = Guid.NewGuid(), WorkspaceId = workspaceId, FileName = "doc2.txt" };
        
        var documents = new List<WorkspaceDocument> { doc1, doc2 };

        _workspaceDocumentRepository.FindAsync(default!, default!, default!)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<WorkspaceDocument>>(documents));

        _storage.GetExtractedTextAsync(doc1, Arg.Any<CancellationToken>())
            .Returns("{\"FullText\": \"Hello from doc1\"}");
        _storage.GetExtractedTextAsync(doc2, Arg.Any<CancellationToken>())
            .Returns("Plain text fallback doc2");

        // Act
        await _service.ProcessContextSnapshotAsync(roomId, workspaceId, CancellationToken.None);

        // Assert
        await _db.ReceivedWithAnyArgs(1).StringSetAsync(default(RedisKey), default(RedisValue));
    }
}
