using System;
using System.Collections.Generic;
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
    public const string DeliveryStreamName = "admin-notifications-delivery";
    public const int DeliveryChunkSize = 1000;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IValidator<CreateAdminNotificationDto> _validator;
    private readonly IMessagePublisher _messagePublisher;
    private readonly ILogger<AdminNotificationService> _logger;
    private readonly IAdminAudienceResolver? _audienceResolver;

    public AdminNotificationService(
        IUnitOfWork unitOfWork,
        IValidator<CreateAdminNotificationDto> validator,
        IMessagePublisher messagePublisher,
        ILogger<AdminNotificationService> logger,
        IAdminAudienceResolver? audienceResolver = null)
    {
        _unitOfWork = unitOfWork;
        _validator = validator;
        _messagePublisher = messagePublisher;
        _logger = logger;
        _audienceResolver = audienceResolver;
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

        // The row records the audience as the admin DEFINED it (mode + segment), not the
        // thousands of ids a broadcast resolves to — so it is mapped before resolution.
        var notification = AdminNotificationMapper.ToEntity(request, adminId);

        // WT-699 / TC4104: BROADCAST / SEGMENT become explicit ids here, once, so delivery is the
        // same chunked pipeline for every mode.
        if (request.TargetAudienceMode is Domain.Constants.NotificationConstants.TargetModeBroadcast
            or Domain.Constants.NotificationConstants.TargetModeSegment)
        {
            if (_audienceResolver is null)
            {
                return Result.Failure<AdminNotification>(
                    "Sending to everyone or to a workspace is not configured on this deployment.",
                    ErrorCodes.ServiceUnavailable);
            }

            var audience = await _audienceResolver.ResolveAsync(request.TargetAudienceMode, request.SegmentId, ct);
            if (!audience.IsSuccess)
                return Result.Failure<AdminNotification>(audience.Error ?? "Could not resolve the audience.", audience.ErrorCode);

            var recipients = audience.Value!.Distinct().ToList();
            if (recipients.Count == 0)
            {
                return Result.Failure<AdminNotification>(
                    "Nobody is in this audience, so there is nobody to send it to.", ErrorCodes.ValidationError);
            }

            request = request with { SpecificUserIds = recipients };
        }

        // Decide the delivery events before the row is written: the consumer flips the
        // announcement to Sent only when it has processed this many of them.
        var payloads = BuildDeliveryPayloads(notification.Id, request);
        notification.DeliveryChunkCount = payloads.Count;

        await _unitOfWork.AdminNotificationRepository.AddAsync(notification, ct);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Admin notification created successfully with ID {NotificationId} by Admin {AdminId}", notification.Id, adminId);

        // Chunking Strategy for Delivery Trigger (T015)
        // Prepare events for downstream workers via Redis Streams (US4)
        try
        {
            foreach (var payload in payloads)
            {
                await _messagePublisher.PublishAsync(DeliveryStreamName, payload, ct);
                _logger.LogInformation(
                    "Published delivery event of {Count} users for Notification {NotificationId} with Mode {Mode}.",
                    payload.SpecificUserIds?.Length ?? 0, notification.Id, payload.TargetAudienceMode);
            }
        }
        catch (Exception ex)
        {
            // The row is committed and some chunks may already be on the stream, so this
            // announcement will never reach everyone. Say so rather than leaving it Pending.
            _logger.LogError(ex, "Publishing delivery events failed for Notification {NotificationId}; marking it Failed.", notification.Id);
            await _unitOfWork.AdminNotificationRepository.MarkFailedAsync(notification.Id, CancellationToken.None);
            throw;
        }

        return Result.Success(notification);
    }

    private static List<DeliveryEventPayload> BuildDeliveryPayloads(Guid notificationId, CreateAdminNotificationDto request)
    {
        if (request.SpecificUserIds is { Count: > 0 })
        {
            return request.SpecificUserIds
                .Chunk(DeliveryChunkSize)
                .Select(chunk => new DeliveryEventPayload(notificationId, request.TargetAudienceMode, chunk))
                .ToList();
        }

        return [new DeliveryEventPayload(notificationId, request.TargetAudienceMode, null)];
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
