using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>
/// Request to deduct usage quota
/// </summary>
/// <example>
/// {
///   "workspaceId": "77777777-7777-7777-7777-777777777777",
///   "sessionId": "00000000-0000-0000-0000-000000000001",
///   "consumedMinutes": 15.5,
///   "source": "Weekly Sync Meeting"
/// }
/// </example>
public class QuotaDeductRequest : IValidatableObject
{
    public QuotaDeductRequest()
    {
    }

    public QuotaDeductRequest(Guid sessionId, decimal consumedMinutes, string source)
    {
        SessionId = sessionId;
        ConsumedMinutes = consumedMinutes;
        Source = source;
    }

    [Required]
    public Guid SessionId { get; set; }

    [Range(typeof(decimal), "0.01", "10000")]
    public decimal ConsumedMinutes { get; set; }

    [Required]
    [StringLength(100)]
    public string Source { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SessionId == Guid.Empty)
        {
            yield return new ValidationResult("SessionId cannot be empty.", [nameof(SessionId)]);
        }

        if (string.IsNullOrWhiteSpace(Source))
        {
            yield return new ValidationResult("Source is required.", [nameof(Source)]);
        }
    }
}
