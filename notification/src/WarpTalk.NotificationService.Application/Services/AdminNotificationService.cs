using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using Microsoft.Extensions.Logging;
using WarpTalk.NotificationService.Application.DTOs.AdminNotifications;
using WarpTalk.NotificationService.Application.Helpers.AdminNotifications;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Application.Mappers;
using WarpTalk.Shared;

namespace WarpTalk.NotificationService.Application.Services;

public class AdminNotificationService : IAdminNotificationService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IValidator<CreateAdminNotificationDto> _validator;
    private readonly IMessagePublisher _messagePublisher;
    private readonly ILogger<AdminNotificationService> _logger;

    public AdminNotificationService(
        IUnitOfWork unitOfWork,
        IValidator<CreateAdminNotificationDto> validator,
        IMessagePublisher messagePublisher,
        ILogger<AdminNotificationService> logger)
    {
        _unitOfWork = unitOfWork;
        _validator = validator;
        _messagePublisher = messagePublisher;
        _logger = logger;
    }

    public async Task<Result<AdminNotification>> CreateAdminNotificationAsync(Guid adminId, CreateAdminNotificationDto request, CancellationToken ct = default)
    {
        var validationResult = await _validator.ValidateAsync(request, ct);
        if (!validationResult.IsValid)
        {
            var firstError = validationResult.Errors.First().ErrorMessage;
            _logger.LogWarning("Validation failed for admin notification: {Error}", firstError);
            return Result.Failure<AdminNotification>(firstError, ErrorCodes.ValidationError);
        }

        if (request.TargetAudienceMode == Domain.Constants.NotificationConstants.TargetModeSpecificUsers)
        {
            var (isValid, userIds, errorMessage) = NotificationValidationHelper.DeduplicateAndValidateUserIds(request.SpecificUserIds);
            if (!isValid)
            {
                return Result.Failure<AdminNotification>(errorMessage ?? "Invalid specific users.", ErrorCodes.ValidationError);
            }
            
            // Replace the original list with the deduplicated one
            request = request with { SpecificUserIds = userIds };
        }

        var notification = AdminNotificationMapper.ToEntity(request, adminId);

        await _unitOfWork.AdminNotificationRepository.AddAsync(notification, ct);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Admin notification created successfully with ID {NotificationId} by Admin {AdminId}", notification.Id, adminId);

        // Chunking Strategy for Delivery Trigger (T015)
        // Prepare events for downstream workers via Redis Streams (US4)
        if (request.TargetAudienceMode == Domain.Constants.NotificationConstants.TargetModeSpecificUsers && request.SpecificUserIds != null)
        {
            var userChunks = request.SpecificUserIds.Chunk(1000);
            foreach (var chunk in userChunks)
            {
                var payload = new Application.DTOs.AdminNotifications.DeliveryEventPayload(
                    notification.Id,
                    request.TargetAudienceMode,
                    chunk
                );
                await _messagePublisher.PublishAsync("admin-notifications-delivery", payload, ct);
                _logger.LogInformation("Published chunk of {Count} users for Notification {NotificationId} to Redis Streams.", chunk.Length, notification.Id);
            }
        }
        else
        {
            var payload = new Application.DTOs.AdminNotifications.DeliveryEventPayload(
                notification.Id,
                request.TargetAudienceMode,
                null
            );
            await _messagePublisher.PublishAsync("admin-notifications-delivery", payload, ct);
            _logger.LogInformation("Published delivery event for Notification {NotificationId} with Mode {Mode}.", notification.Id, request.TargetAudienceMode);
        }

        return Result.Success(notification);
    }

    public async Task<Result<AdminNotificationPaginatedResponse>> GetAdminNotificationsAsync(GetAdminNotificationsQuery query, CancellationToken ct = default)
    {
        var filter = new Domain.Models.AdminNotificationFilter(
            query.Page, query.PageSize, query.Title, query.Type, query.Status, query.CreatedFrom, query.CreatedTo
        );

        var (items, totalCount) = await _unitOfWork.AdminNotificationRepository.GetPaginatedAsync(filter, ct);

        var dtos = items.Select(AdminNotificationMapper.ToSummaryDto);

        var response = new AdminNotificationPaginatedResponse(dtos, totalCount, query.Page, query.PageSize);
        return Result.Success(response);
    }

    public async Task<Result<AdminNotificationDetailDto>> GetAdminNotificationDetailAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _unitOfWork.AdminNotificationRepository.GetByIdAsync(id, ct);
        if (entity == null)
        {
            return Result.Failure<AdminNotificationDetailDto>($"Admin Notification with ID {id} not found.", ErrorCodes.NotFound);
        }

        return Result.Success(AdminNotificationMapper.ToDetailDto(entity));
    }
}
