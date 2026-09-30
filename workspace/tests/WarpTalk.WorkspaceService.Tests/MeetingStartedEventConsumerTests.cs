using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;
using WarpTalk.Shared.Events;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class MeetingStartedEventConsumerTests
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly IServiceProvider _serviceProvider;
    private readonly IServiceScope _serviceScope;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceDocumentRepository _workspaceDocumentRepository;
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
        _workspaceDocumentRepository = Substitute.For<IWorkspaceDocumentRepository>();
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
    public void TryParseEvent_AcceptsVersionedMeetingStartedEnvelope()
    {
        var roomId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var envelope = DomainEventEnvelope.Create(
            MeetingEventTypes.Started,
            "meeting-service",
            workspaceId.ToString(),
            new MeetingStartedEventPayload(roomId, workspaceId));

        var parsed = MeetingStartedEventConsumer.TryParseEvent(
            JsonSerializer.Serialize(envelope),
            out var payload);

        Assert.True(parsed);
        Assert.Equal(roomId, payload!.TranslationRoomId);
        Assert.Equal(workspaceId, payload.WorkspaceId);
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
        var doc1 = EligibleDocument(workspaceId, "doc1.txt");
        var doc2 = EligibleDocument(workspaceId, "doc2.txt");

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

    // ---- WT-872: the snapshot is handed to the model verbatim ---------------------------------
    //
    // AiEligible records that a document WAS indexed. A document rejected, still pending approval
    // or made private afterwards keeps that flag until something clears it, so the snapshot asks
    // the embedding pipeline's own question — IsIndexEligible — before reading any text.

    private static WorkspaceDocument EligibleDocument(Guid workspaceId, string fileName) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        FileName = fileName,
        Status = WorkspaceDocumentStatus.@public.ToString(),
        RetentionState = WorkspaceDocumentConstants.RetentionStateActive,
        ConfidentialityLevel = WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel,
        IngestionStatus = WorkspaceDocumentIngestionStatus.completed.ToString(),
        LastIndexedAt = DateTime.UtcNow,
        IsAiAllowed = true,
        AiEligible = true,
    };

    [Theory]
    [InlineData("pending_approval")]
    [InlineData("rejected")]
    [InlineData("private")]
    public async Task ProcessContextSnapshotAsync_ShouldLeaveOutDocumentsThatAreNoLongerAiEligible(string status)
    {
        var roomId = "room-872";
        var workspaceId = Guid.NewGuid();
        var approved = EligibleDocument(workspaceId, "approved.txt");
        var withheld = EligibleDocument(workspaceId, "withheld.txt");
        withheld.Status = status;

        _workspaceDocumentRepository.FindAsync(default!, default!, default!)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<WorkspaceDocument>>(new List<WorkspaceDocument> { approved, withheld }));
        _storage.GetExtractedTextAsync(approved, Arg.Any<CancellationToken>())
            .Returns("{\"FullText\": \"Quarterly plan\"}");
        _storage.GetExtractedTextAsync(withheld, Arg.Any<CancellationToken>())
            .Returns("{\"FullText\": \"Mat ma Omega 99\"}");

        await _service.ProcessContextSnapshotAsync(roomId, workspaceId, CancellationToken.None);

        await _storage.DidNotReceive().GetExtractedTextAsync(withheld, Arg.Any<CancellationToken>());
        var snapshot = SnapshotWritten();
        Assert.Contains("Quarterly plan", snapshot);
        Assert.DoesNotContain("Omega 99", snapshot);
    }

    [Fact]
    public async Task ProcessContextSnapshotAsync_ShouldNotSetRedis_WhenEveryDocumentIsWithheld()
    {
        var workspaceId = Guid.NewGuid();
        var pending = EligibleDocument(workspaceId, "pending.txt");
        pending.Status = WorkspaceDocumentStatus.pending_approval.ToString();

        _workspaceDocumentRepository.FindAsync(default!, default!, default!)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<WorkspaceDocument>>(new List<WorkspaceDocument> { pending }));

        await _service.ProcessContextSnapshotAsync("room-872", workspaceId, CancellationToken.None);

        await _storage.DidNotReceiveWithAnyArgs().GetExtractedTextAsync(default!, default);
        Assert.DoesNotContain(_db.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync));
    }

    private string SnapshotWritten()
    {
        var call = Assert.Single(_db.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync));
        return ((RedisValue)call.GetArguments()[1]!).ToString();
    }
}
