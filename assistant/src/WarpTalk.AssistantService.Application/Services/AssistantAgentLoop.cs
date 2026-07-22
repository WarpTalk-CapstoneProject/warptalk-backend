using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Interfaces;

namespace WarpTalk.AssistantService.Application.Services;

/// <summary>
/// Drives one assistant turn: loads conversation history, streams the agent's reply through
/// IAssistantAgent, forwards coalesced chunks to the frontend via IAssistantNotifier, and
/// persists the final message. Milestone A registers zero tools — availableTools is empty,
/// so AssistantToolCallRequested events cannot occur yet; the branch below exists so tool
/// dispatch (Milestone B/C) is a pure addition, not a rewrite of this loop.
/// </summary>
public class AssistantAgentLoop
{
    // Flush the streamed chunk to the hub every ~40 characters rather than per-token —
    // matches how STT/TTS/AI-assistant results already arrive as coarse buffered units
    // elsewhere in this codebase, at a fraction of the SignalR message volume.
    private const int ChunkFlushThreshold = 40;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IAssistantAgent _agent;
    private readonly IAssistantNotifier _notifier;
    private readonly ILogger<AssistantAgentLoop> _logger;

    public AssistantAgentLoop(
        IUnitOfWork unitOfWork,
        IAssistantAgent agent,
        IAssistantNotifier notifier,
        ILogger<AssistantAgentLoop> logger)
    {
        _unitOfWork = unitOfWork;
        _agent = agent;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task RunAsync(AssistantAgentJob job, CancellationToken ct = default)
    {
        var assistantMessage = await _unitOfWork.AssistantMessageRepository.GetByIdAsync(job.AssistantMessageId, ct);
        if (assistantMessage == null)
        {
            _logger.LogWarning("AssistantAgentLoop: assistant message {MessageId} not found, skipping job.", job.AssistantMessageId);
            return;
        }

        await _notifier.BroadcastMessageStartedAsync(job.ConversationId, job.AssistantMessageId, ct);

        try
        {
            var history = await LoadHistoryAsync(job.ConversationId, job.AssistantMessageId, ct);
            var buffer = new StringBuilder();
            var finalText = string.Empty;

            await foreach (var evt in _agent.RunAsync(history, Array.Empty<AssistantToolDefinition>(), ct))
            {
                switch (evt)
                {
                    case AssistantTextDelta delta:
                        buffer.Append(delta.Delta);
                        if (buffer.Length >= ChunkFlushThreshold)
                        {
                            await _notifier.BroadcastMessageChunkAsync(job.ConversationId, job.AssistantMessageId, buffer.ToString(), ct);
                            buffer.Clear();
                        }
                        break;

                    case AssistantToolCallRequested:
                        // No tools registered in Milestone A — nothing to dispatch to yet.
                        _logger.LogWarning("AssistantAgentLoop: received a tool call with no tools registered; ignoring.");
                        break;

                    case AssistantCompleted completed:
                        if (buffer.Length > 0)
                        {
                            await _notifier.BroadcastMessageChunkAsync(job.ConversationId, job.AssistantMessageId, buffer.ToString(), ct);
                            buffer.Clear();
                        }
                        finalText = completed.FinalText;
                        break;
                }
            }

            assistantMessage.Content = finalText;
            assistantMessage.Status = "completed";
            assistantMessage.CompletedAt = DateTime.UtcNow;
            _unitOfWork.AssistantMessageRepository.Update(assistantMessage);
            await _unitOfWork.SaveChangesAsync(ct);

            await _notifier.BroadcastMessageCompletedAsync(job.ConversationId, assistantMessage.ToDto(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AssistantAgentLoop: agent turn failed for message {MessageId}.", job.AssistantMessageId);

            assistantMessage.Status = "failed";
            assistantMessage.CompletedAt = DateTime.UtcNow;
            _unitOfWork.AssistantMessageRepository.Update(assistantMessage);
            await _unitOfWork.SaveChangesAsync(ct);

            await _notifier.BroadcastMessageFailedAsync(job.ConversationId, job.AssistantMessageId, "The assistant could not generate a reply.", ct);
        }
    }

    private async Task<IReadOnlyList<AssistantChatTurn>> LoadHistoryAsync(Guid conversationId, Guid excludeMessageId, CancellationToken ct)
    {
        var messages = await _unitOfWork.AssistantMessageRepository.FindAsync(
            m => m.ConversationId == conversationId && m.Id != excludeMessageId && m.Status == "completed", ct: ct);

        return messages
            .OrderBy(m => m.CreatedAt)
            .Select(m => new AssistantChatTurn(m.Role, m.Content))
            .ToList();
    }
}
