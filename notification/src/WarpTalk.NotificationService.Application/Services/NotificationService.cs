using WarpTalk.Shared;
using WarpTalk.NotificationService.Application.DTOs;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Domain.Entities;
using Microsoft.Extensions.Logging;
using WarpTalk.NotificationService.Application.Mappers;
using WarpTalk.NotificationService.Domain.Constants;

namespace WarpTalk.NotificationService.Application.Services;

public class NotificationService : INotificationService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(IUnitOfWork unitOfWork, ILogger<NotificationService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<NotificationPreferenceDto>> GetPreferencesAsync(Guid userId, CancellationToken ct = default)
    {
        var pref = await _unitOfWork.NotificationPreferenceRepository.GetByUserIdAsync(userId, ct);
        
        if (pref == null)
        {
            pref = NotificationPreferenceMapper.CreateDefaultEntity(userId);
            await _unitOfWork.NotificationPreferenceRepository.AddAsync(pref);
            await _unitOfWork.SaveChangesAsync();
        }

        return Result.Success(NotificationPreferenceMapper.ToDto(pref));
    }

    public async Task<Result<NotificationPreferenceDto>> UpdatePreferencesAsync(Guid userId, UpdateNotificationPreferenceRequest request, CancellationToken ct = default)
    {
        var pref = await _unitOfWork.NotificationPreferenceRepository.GetByUserIdAsync(userId, ct);

        if (pref == null)
            return Result.Failure<NotificationPreferenceDto>(NotificationConstants.ErrorPreferencesNotFound, ErrorCodes.NotFound);

        NotificationPreferenceMapper.ApplyUpdate(pref, request);
        _unitOfWork.NotificationPreferenceRepository.Update(pref);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(NotificationPreferenceMapper.ToDto(pref));
    }

    public async Task<Result> SendNotificationAsync(Guid userId, string templateCode, Dictionary<string, string> variables, CancellationToken ct = default)
    {
        // Mock notification send logic
        await Task.Delay(100, ct);
        return Result.Success();
    }

    public async Task<Result<NotificationPaginatedResponse>> GetNotificationsAsync(Guid userId, int page = 1, int pageSize = NotificationConstants.DefaultPageSize, CancellationToken ct = default)
    {
        pageSize = Math.Max(1, Math.Min(pageSize, NotificationConstants.MaxPageSize)); // Enforce bounded resource behavior
        var (items, count) = await _unitOfWork.NotificationMessageRepository.GetPaginatedByUserIdAsync(userId, page, pageSize, ct);

        var dtoItems = items.Select(NotificationMessageMapper.ToDto);

        return Result.Success(new NotificationPaginatedResponse(dtoItems, count, page, pageSize));
    }

    public async Task<Result> MarkAsReadAsync(Guid userId, Guid notificationId, CancellationToken ct = default)
    {
        var notification = await _unitOfWork.NotificationMessageRepository.GetByIdAndUserIdAsync(notificationId, userId, ct);
        
        if (notification == null)
            return Result.Failure(NotificationConstants.ErrorNotificationNotFound, ErrorCodes.NotFound);
            
        if (!notification.IsRead)
        {
            await _unitOfWork.NotificationMessageRepository.MarkAsReadAsync(notificationId, userId, ct);
        }
        
        return Result.Success();
    }

    public async Task<Result> MarkAllAsReadAsync(Guid userId, CancellationToken ct = default)
    {
        await _unitOfWork.NotificationMessageRepository.MarkAllAsReadAsync(userId, ct);
        return Result.Success();
    }

    public async Task<Result<NotificationMessageDto>> CreateNotificationAsync(CreateNotificationMessageDto dto, CancellationToken ct = default)
    {
        var notification = NotificationMessageMapper.ToEntity(dto);
        
        await _unitOfWork.NotificationMessageRepository.AddAsync(notification);
        await _unitOfWork.SaveChangesAsync();
        
        var resultDto = NotificationMessageMapper.ToDto(notification);
        
        return Result.Success(resultDto);
    }

}
