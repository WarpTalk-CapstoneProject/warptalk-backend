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

        if (!McpConfirmationTokenPayloadMatcher.Matches(payload, userId, pluginId, request))
            return Result.Failure("Confirmation token does not match this plugin action.", PluginConstants.ErrorCodes.PermissionDenied);

        var consumed = await _unitOfWork.PluginConfirmationTokenRepository.TryConsumeAsync(payload.TokenId, now, ct);
        if (!consumed)
            return Result.Failure("Confirmation token has already been used.", PluginConstants.ErrorCodes.PermissionDenied);

        return Result.Success();
    }
}
