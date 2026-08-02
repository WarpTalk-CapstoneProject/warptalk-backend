using System.ComponentModel.DataAnnotations;
using WarpTalk.BillingService.Application.DTOs;

namespace WarpTalk.BillingService.Tests.Application.DTOs;

public sealed class AdjustCreditsRequestValidationTests
{
    [Theory]
    [InlineData(1_000_001)]
    [InlineData(-1_000_001)]
    public void Amount_OutsideAdministrativeLimit_IsRejected(int amount)
    {
        var request = new ManualAdjustCreditsRequest(Guid.NewGuid(), amount, "Manual correction", Guid.NewGuid().ToString());
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(request.Amount)));
    }
}
