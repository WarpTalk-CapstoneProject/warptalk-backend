using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Infrastructure.Clients;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class HybridWorkspaceDocumentEventPublisherTests
{
    private readonly IConnectionMultiplexer _redis = Substitute.For<IConnectionMultiplexer>();
    private readonly IDatabase _database = Substitute.For<IDatabase>();
    private readonly IPublishEndpoint _publishEndpoint = Substitute.For<IPublishEndpoint>();
    private readonly HybridWorkspaceDocumentEventPublisher _publisher;

    public HybridWorkspaceDocumentEventPublisherTests()
    {
        _redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(_database);
        _database.StreamAddAsync(Arg.Any<RedisKey>(), Arg.Any<NameValueEntry[]>())
            .Returns(Task.FromResult((RedisValue)"1-0"));

        _publisher = new HybridWorkspaceDocumentEventPublisher(
            _redis,
            _publishEndpoint,
            Substitute.For<ILogger<HybridWorkspaceDocumentEventPublisher>>());
    }

    [Fact]
    public async Task PublishDocumentUploadedAsync_PublishesRabbitMqEventAndRedisCompatibilityStream()
    {
        var documentId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _publisher.PublishDocumentUploadedAsync(
            documentId,
            workspaceId,
            "workspace/doc.pdf",
            "doc.pdf",
            ".pdf",
            userId,
            isSensitive: true,
            CancellationToken.None);

        await _publishEndpoint.Received(1).Publish(
            Arg.Is<WorkspaceDocumentIngestionRequestedEvent>(message =>
                message.DocumentId == documentId.ToString() &&
                message.WorkspaceId == workspaceId.ToString() &&
                message.StorageKey == "workspace/doc.pdf" &&
                message.FileName == "doc.pdf" &&
                message.FileExtension == ".pdf" &&
                message.RequestedByUserId == userId.ToString() &&
                message.IsSensitive),
            Arg.Any<CancellationToken>());

        await _database.Received(1).StreamAddAsync(
            "workspace-document-events",
            Arg.Is<NameValueEntry[]>(entries =>
                Contains(entries, "event_type", "DocumentUploaded") &&
                Contains(entries, "document_id", documentId.ToString()) &&
                Contains(entries, "workspace_id", workspaceId.ToString()) &&
                Contains(entries, "storage_key", "workspace/doc.pdf") &&
                Contains(entries, "is_sensitive", "True")));
    }

    [Fact]
    public async Task PublishDocumentDeletedAsync_PublishesRabbitMqInvalidationAndRedisCompatibilityStream()
    {
        var documentId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        await _publisher.PublishDocumentDeletedAsync(documentId, workspaceId);

        await _publishEndpoint.Received(1).Publish(
            Arg.Is<WorkspaceDocumentInvalidatedEvent>(message =>
                message.DocumentId == documentId.ToString() &&
                message.WorkspaceId == workspaceId.ToString() &&
                message.Reason == "deleted"),
            Arg.Any<CancellationToken>());

        await _database.Received(1).StreamAddAsync(
            "workspace-document-events",
            Arg.Is<NameValueEntry[]>(entries =>
                Contains(entries, "event_type", "DocumentDeleted") &&
                Contains(entries, "document_id", documentId.ToString()) &&
                Contains(entries, "workspace_id", workspaceId.ToString())));
    }

    private static bool Contains(NameValueEntry[] entries, string name, string value)
    {
        return entries.Any(entry => entry.Name == name && entry.Value == value);
    }
}
