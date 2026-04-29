using System;

namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>
/// Request to deduct usage quota
/// </summary>
/// <param name="SessionId">Unique identifier of the meeting session (Idempotency Key)</param>
/// <param name="ConsumedMinutes">Amount of minutes to deduct</param>
/// <param name="Source">Source of the deduction (e.g., meeting description)</param>
/// <example>
/// {
///   "workspaceId": "77777777-7777-7777-7777-777777777777",
///   "sessionId": "00000000-0000-0000-0000-000000000001",
///   "consumedMinutes": 15.5,
///   "source": "Weekly Sync Meeting"
/// }
/// </example>
public record QuotaDeductRequest(
    Guid SessionId,
    decimal ConsumedMinutes,
    string Source
);
