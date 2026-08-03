using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.API.Consumers;

/// <summary>
/// Appends admin actions performed by other services to the audit log this service owns
/// (WT-210). Billing, transcript, and notification keep their own logical databases, so the
/// bus — not a cross-database write — is how their actions become queryable here.
/// </summary>
public sealed class AdminActionRecordedConsumer : IConsumer<AdminActionRecordedEvent>
{
    private readonly IAdminAuditLogService _adminAuditLogService;
    private readonly ILogger<AdminActionRecordedConsumer> _logger;

    public AdminActionRecordedConsumer(
        IAdminAuditLogService adminAuditLogService,
        ILogger<AdminActionRecordedConsumer> logger)
    {
        _adminAuditLogService = adminAuditLogService;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<AdminActionRecordedEvent> context)
    {
        var result = await _adminAuditLogService.RecordAsync(context.Message, context.CancellationToken);

        if (!result.IsSuccess)
        {
            _logger.LogError(
                "Rejected admin audit entry from {Source}: {Error}",
                context.Message.SourceService,
                result.Error);

            // Rethrow so MassTransit retries and eventually dead-letters. Losing an audit
            // record silently is worse than a poison message sitting in the error queue.
            throw new AdminAuditRecordFailedException(result.Error ?? "Failed to record admin action.");
        }
    }
}

public sealed class AdminAuditRecordFailedException(string message) : System.Exception(message);
