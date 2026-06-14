using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Clients;

public class RedisDocumentEventPublisher : IWorkspaceDocumentEventPublisher
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisDocumentEventPublisher> _logger;

    public RedisDocumentEventPublisher(IConnectionMultiplexer redis, ILogger<RedisDocumentEventPublisher> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task PublishDocumentUploadedAsync(
        Guid documentId,
        Guid workspaceId,
        string storageKey,
        string fileName,
        string fileExtension,
        Guid userId,
        bool isSensitive,
        CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.StreamAddAsync("workspace-document-events", new NameValueEntry[]
            {
                new NameValueEntry("event_type", "DocumentUploaded"),
                new NameValueEntry("document_id", documentId.ToString()),
                new NameValueEntry("workspace_id", workspaceId.ToString()),
                new NameValueEntry("storage_key", storageKey),
                new NameValueEntry("file_name", fileName),
                new NameValueEntry("file_extension", fileExtension),
                new NameValueEntry("uploaded_by", userId.ToString()),
                new NameValueEntry("is_sensitive", isSensitive.ToString())
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish document upload event to Redis Stream. DocumentId: {DocumentId}", documentId);
        }
    }

    public async Task PublishDocumentDeletedAsync(Guid documentId, Guid workspaceId, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.StreamAddAsync("workspace-document-events", new NameValueEntry[]
            {
                new NameValueEntry("event_type", "DocumentDeleted"),
                new NameValueEntry("document_id", documentId.ToString()),
                new NameValueEntry("workspace_id", workspaceId.ToString())
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish document delete event to Redis Stream. DocumentId: {DocumentId}", documentId);
        }
    }

    public async Task PublishDocumentArchivedAsync(Guid documentId, Guid workspaceId, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.StreamAddAsync("workspace-document-events", new NameValueEntry[]
            {
                new NameValueEntry("event_type", "DocumentArchived"),
                new NameValueEntry("document_id", documentId.ToString()),
                new NameValueEntry("workspace_id", workspaceId.ToString())
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish document archive event to Redis Stream. DocumentId: {DocumentId}", documentId);
        }
    }
}
