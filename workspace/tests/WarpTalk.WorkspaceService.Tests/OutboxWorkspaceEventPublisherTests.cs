using System.Text.Json;
using NSubstitute;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Outbox;

namespace WarpTalk.WorkspaceService.Tests;

public sealed class OutboxWorkspaceEventPublisherTests
{
    [Fact]
    public async Task PublishMemberRoleChangedAsync_EnqueuesStableRoleChangedPayload()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var repository = Substitute.For<IWorkspaceOutboxMessageRepository>();
        unitOfWork.WorkspaceOutboxMessageRepository.Returns(repository);
        var publisher = new OutboxWorkspaceEventPublisher(new WorkspaceOutboxWriter(unitOfWork));
        var workspaceId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var effectiveAt = DateTime.UtcNow;

        await publisher.PublishMemberRoleChangedAsync(
            workspaceId,
            targetUserId,
            "Member",
            "Admin",
            actorUserId,
            eventId,
            "correlation-1",
            "Internal",
            "next-request-or-session",
            effectiveAt,
            "idem-1");

        await repository.Received(1).AddAsync(
            Arg.Is<WorkspaceOutboxMessage>(message =>
                message.EventType == "workspace.member.role_changed"
                && message.CompatibilityEventType == "MemberRoleChanged"
                && HasRoleChangedPayload(message.PayloadJson, eventId, "correlation-1", "idem-1")),
            Arg.Any<CancellationToken>());
    }

    private static bool HasRoleChangedPayload(
        string json,
        Guid eventId,
        string correlationId,
        string idempotencyKey)
    {
        var payload = JsonSerializer.Deserialize<MemberRoleChangedEventPayload>(json);
        return payload is
        {
            OldRole: "Member",
            NewRole: "Admin",
            MembershipType: "Internal",
            EffectiveBehavior: "next-request-or-session"
        }
        && payload.EventId == eventId.ToString()
        && payload.CorrelationId == correlationId
        && payload.IdempotencyKey == idempotencyKey;
    }
}
