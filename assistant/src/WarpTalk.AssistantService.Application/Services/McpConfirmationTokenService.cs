using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

public class McpConfirmationTokenService : IMcpConfirmationTokenService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMcpConfirmationTokenProtector _protector;

    public McpConfirmationTokenService(
        IUnitOfWork unitOfWork,
        IMcpConfirmationTokenProtector protector)
    {
        _unitOfWork = unitOfWork;
        _protector = protector;
    }

    public async Task<Result<string>> CreateAsync(
        Guid userId,
        Guid pluginId,
        McpToolExecutionRequest request,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var expiresAt = now.AddMinutes(PluginConstants.ConfirmationTokenLifetimeMinutes);
        var entity = McpConfirmationTokenMapper.ToEntity(userId, pluginId, request, now, expiresAt);

        await _unitOfWork.PluginConfirmationTokenRepository.AddAsync(entity, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(_protector.Protect(McpConfirmationTokenMapper.ToPayload(entity)));
    }

    public async Task<Result> ValidateAndConsumeAsync(
        Guid userId,
        Guid pluginId,
        McpToolExecutionRequest request,
        string token,
        CancellationToken ct = default)
    {
        var payloadResult = _protector.Unprotect(token);
        if (!payloadResult.IsSuccess || payloadResult.Value == null)
            return Result.Failure("Confirmation token is invalid.", PluginConstants.ErrorCodes.PermissionDenied);

        var payload = payloadResult.Value;
        var now = DateTime.UtcNow;
        if (payload.ExpiresAt <= now)
            return Result.Failure("Confirmation token expired. Confirm this action again.", PluginConstants.ErrorCodes.ConfirmationRequired);

        if (!McpConfirmationTokenPayloadMatcher.MatchesAction(payload, userId, pluginId, request))
            return Result.Failure("Confirmation token does not match this plugin action.", PluginConstants.ErrorCodes.PermissionDenied);

        // Same tool, different arguments: the call is not the one the user confirmed, so it must
        // not run — but it is a new action to confirm rather than an attempt to slip past the
        // gate. Asking again is the only way out; permission_denied here was a dead end, and the
        // assistant reaches it by itself whenever it re-sends a title it had to invent.
        if (!McpConfirmationTokenPayloadMatcher.MatchesArguments(payload, request))
            return Result.Failure(
                "This is not the action you confirmed. Confirm the new one before WarpBot runs it.",
                PluginConstants.ErrorCodes.ConfirmationRequired);

        var consumed = await _unitOfWork.PluginConfirmationTokenRepository.TryConsumeAsync(payload.TokenId, now, ct);
        if (!consumed)
            return Result.Failure("Confirmation token has already been used.", PluginConstants.ErrorCodes.PermissionDenied);

        return Result.Success();
    }
}
