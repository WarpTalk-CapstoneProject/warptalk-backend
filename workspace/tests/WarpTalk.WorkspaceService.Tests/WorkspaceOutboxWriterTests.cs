using System.Text.Json;
using NSubstitute;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Outbox;

namespace WarpTalk.WorkspaceService.Tests;

public sealed class WorkspaceOutboxWriterTests
{
    [Fact]
    public async Task EnqueueAsync_PreservesEnvelopeIdentityAndPayload()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var repository = Substitute.For<IWorkspaceOutboxMessageRepository>();
        unitOfWork.WorkspaceOutboxMessageRepository.Returns(repository);
        var writer = new WorkspaceOutboxWriter(unitOfWork);
        var workspaceId = Guid.NewGuid();
        var occurredAt = DateTime.UtcNow;
        var envelope = new EventEnvelope<WorkspaceCreatedEventPayload>(
            Guid.NewGuid(),
            WorkspaceEventTypes.WorkspaceCreated,
            1,
            occurredAt,
            WorkspaceEventTypes.Producer,
            "correlation",
            "causation",
            workspaceId.ToString(),
            new WorkspaceCreatedEventPayload(
                workspaceId.ToString(),
                "WarpTalk",
                "warptalk",
                Guid.NewGuid().ToString(),
                occurredAt));

        await writer.EnqueueAsync(envelope, "WorkspaceCreated");

        await repository.Received(1).AddAsync(
            Arg.Is<WorkspaceOutboxMessage>(message =>
                message.Id == envelope.EventId
                && message.EventType == envelope.EventType
                && message.SchemaVersion == envelope.SchemaVersion
                && message.OccurredAt == envelope.OccurredAt
                && message.Producer == envelope.Producer
                && message.CorrelationId == envelope.CorrelationId
                && message.CausationId == envelope.CausationId
                && message.WorkspaceId == workspaceId
                && message.CompatibilityEventType == "WorkspaceCreated"
                && JsonSerializer.Deserialize<WorkspaceCreatedEventPayload>(message.PayloadJson)!.Name == "WarpTalk"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueAsync_DoesNotCommitOutsideOwningBusinessTransaction()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var repository = Substitute.For<IWorkspaceOutboxMessageRepository>();
        unitOfWork.WorkspaceOutboxMessageRepository.Returns(repository);
        var writer = new WorkspaceOutboxWriter(unitOfWork);
        var envelope = DomainEventEnvelope.Create(
            WorkspaceEventTypes.DocumentInvalidated,
            WorkspaceEventTypes.Producer,
            Guid.NewGuid().ToString(),
            new WorkspaceDocumentInvalidatedEventPayload(
                Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString(),
                "deleted"));

        await writer.EnqueueAsync(envelope, "DocumentDeleted");

        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
