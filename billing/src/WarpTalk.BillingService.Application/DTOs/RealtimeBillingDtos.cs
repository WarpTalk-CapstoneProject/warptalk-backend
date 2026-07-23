using System;

namespace WarpTalk.BillingService.Application.DTOs;

public record ReserveCreditsRequest(
    Guid HostWorkspaceId,
    string IdempotencyKey,
    int ParticipantCount = 2,
    string MediaStreamType = "video_sd"
);

public record CreditReservationDto(
    Guid Id,
    Guid SubscriptionId,
    string IdempotencyKey,
    int Amount,
    string Status,
    DateTime ExpiresAt
);
