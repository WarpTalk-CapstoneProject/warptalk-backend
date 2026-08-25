using System.Text.Json;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

public class PluginInstallationService : IPluginInstallationService
{
    private readonly IUnitOfWork _unitOfWork;

    public PluginInstallationService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<IReadOnlyList<PluginCatalogItemDto>>> ListCatalogAsync(Guid userId, CancellationToken ct = default)
    {
        var plugins = await _unitOfWork.PluginRepository.FindAsync(p => p.IsActive, ct: ct);
        var installations = await _unitOfWork.PluginInstallationRepository.FindAsync(i => i.UserId == userId, ct: ct);
        var connections = await _unitOfWork.PluginConnectionRepository.FindAsync(c => c.UserId == userId, ct: ct);

        var items = plugins
            .Select(plugin =>
            {
                var definition = PluginDefinitionMapper.ToDefinition(plugin);
                var installation = installations.FirstOrDefault(i => i.PluginId == plugin.Id);
                var connection = connections.FirstOrDefault(c => c.PluginId == plugin.Id);
                return PluginCatalogItemMapper.ToCatalogItem(definition, installation, connection);
            })
            .ToList();

        return Result.Success<IReadOnlyList<PluginCatalogItemDto>>(items);
    }

    public async Task<Result<PluginCatalogItemDto>> InstallAsync(string pluginKey, Guid userId, CancellationToken ct = default)
    {
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey && p.IsActive, ct: ct);
        if (plugin == null)
            return Result.Failure<PluginCatalogItemDto>("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        var installation = await _unitOfWork.PluginInstallationRepository.FirstOrDefaultAsync(
            i => i.UserId == userId && i.PluginId == plugin.Id, ct: ct);

        if (installation == null)
        {
            installation = new PluginInstallation
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PluginId = plugin.Id,
                Status = PluginConstants.InstallationStatus.Installed,
                ConfigJson = JsonSerializer.Serialize(new { installedFrom = "assistant_plugins" }),
                InstalledAt = DateTime.UtcNow,
            };
            await _unitOfWork.PluginInstallationRepository.AddAsync(installation, ct);
        }
        else
        {
            installation.Status = PluginConstants.InstallationStatus.Installed;
            installation.DisabledAt = null;
            _unitOfWork.PluginInstallationRepository.Update(installation);
        }

        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(PluginCatalogItemMapper.ToCatalogItem(PluginDefinitionMapper.ToDefinition(plugin), installation, null));
    }

    public async Task<Result> DisableAsync(string pluginKey, Guid userId, CancellationToken ct = default)
    {
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey, ct: ct);
        if (plugin == null)
            return Result.Failure("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        var installation = await _unitOfWork.PluginInstallationRepository.FirstOrDefaultAsync(
            i => i.UserId == userId && i.PluginId == plugin.Id, ct: ct);

        if (installation == null)
            return Result.Failure("Plugin is not installed.", PluginConstants.ErrorCodes.PluginNotInstalled);

        installation.Status = PluginConstants.InstallationStatus.Disabled;
        installation.DisabledAt = DateTime.UtcNow;
        _unitOfWork.PluginInstallationRepository.Update(installation);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
