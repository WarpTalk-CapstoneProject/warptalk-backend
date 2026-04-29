using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>
/// Yêu cầu tạo link thanh toán
/// </summary>
public class CreatePaymentLinkRequest : IValidatableObject
{
    /// <summary>
    /// ID của gói cước muốn mua (Upgrade)
    /// </summary>
    public Guid? PlanId { get; set; }

    /// <summary>
    /// Số phút muốn nạp thêm (Top-up)
    /// </summary>
    [Range(typeof(decimal), "0.01", "10000")]
    public decimal? TopUpMinutes { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var hasPlanId = PlanId.HasValue;
        var hasTopUpMinutes = TopUpMinutes.HasValue;

        if (hasPlanId == hasTopUpMinutes)
        {
            yield return new ValidationResult(
                "Must provide exactly one of PlanId or TopUpMinutes.",
                [nameof(PlanId), nameof(TopUpMinutes)]);
        }

        if (PlanId.HasValue && PlanId.Value == Guid.Empty)
        {
            yield return new ValidationResult(
                "PlanId cannot be empty.",
                [nameof(PlanId)]);
        }
    }
}
