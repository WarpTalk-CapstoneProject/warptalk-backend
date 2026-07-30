using NSubstitute;
using StackExchange.Redis;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Clients;
using WarpTalk.WorkspaceService.Infrastructure.Outbox;

namespace WarpTalk.WorkspaceService.Tests;

public sealed class OutboxWorkspaceDocumentEventPublisherTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IGenericRepository<WorkspaceOutboxMessage> _repository =
        Substitute.For<IGenericRepository<WorkspaceOutboxMessage>>();
    private readonly IConnectionMultiplexer _redis =
        Substitute.For<IConnectionMultiplexer>();
    private readonly OutboxWorkspaceDocumentEventPublisher _publisher;

    public OutboxWorkspaceDocumentEventPublisherTests()
    {
        _unitOfWork.Repository<WorkspaceOutboxMessage>().Returns(_repository);
        var auxiliary = new WorkspaceDocumentAuxiliaryPublisher(
            _redis,
            Substitute.For<
                Microsoft.Extensions.Logging.ILogger<WorkspaceDocumentAuxiliaryPublisher>>());
        _publisher = new OutboxWorkspaceDocumentEventPublisher(
            new WorkspaceOutboxWriter(_unitOfWork),
            auxiliary);
    }

    [Fact]
    public async Task PublishDocumentUploadedAsync_EnqueuesWithoutExternalDelivery()
    {
        var documentId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        await _publisher.PublishDocumentUploadedAsync(
            documentId,
            workspaceId,
            "workspace/doc.pdf",
            "doc.pdf",
            ".pdf",
            Guid.NewGuid(),
            "restricted");

        await _repository.Received(1).AddAsync(
            Arg.Is<WorkspaceOutboxMessage>(message =>
                message.EventType == WorkspaceEventTypes.DocumentIngestionRequested
                && message.CompatibilityEventType == "DocumentUploaded"
                && message.WorkspaceId == workspaceId
                && message.PayloadJson.Contains(documentId.ToString())),
            Arg.Any<CancellationToken>());
        _redis.DidNotReceive().GetDatabase(
            Arg.Any<int>(),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task PublishDocumentDeletedAsync_EnqueuesStableInvalidationContract()
    {
        var documentId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        await _publisher.PublishDocumentDeletedAsync(documentId, workspaceId);

        await _repository.Received(1).AddAsync(
            Arg.Is<WorkspaceOutboxMessage>(message =>
                message.EventType == WorkspaceEventTypes.DocumentInvalidated
                && message.CompatibilityEventType == "DocumentDeleted"
                && message.PayloadJson.Contains("\"reason\":\"deleted\"")),
            Arg.Any<CancellationToken>());
    }
}
