using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Options;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Infrastructure.Services;

public sealed class BillingPolicyService : IBillingPolicyService
{
    private const string VatRateConfigKey = "vat_rate";

    private readonly IBillingPolicyRepository _repository;
    private readonly BillingPolicyOptions _seedPolicy;
    private readonly ILogger<BillingPolicyService> _logger;

    public BillingPolicyService(
        IBillingPolicyRepository repository,
        IOptions<BillingPolicyOptions> seedPolicy,
        ILogger<BillingPolicyService> logger)
    {
        _repository = repository;
        _seedPolicy = seedPolicy.Value;
        _logger = logger;
    }

    public async Task<BillingPolicyDto> GetPolicyAsync(CancellationToken cancellationToken = default)
    {

        var vatRate = await _repository.ReadPolicyValueAsync(VatRateConfigKey, _seedPolicy.VatRate!.Value, cancellationToken);
        return new BillingPolicyDto(vatRate);
    }

    public async Task<Result<BillingPolicyDto>> UpdatePolicyAsync(
        UpdateBillingPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.VatRate is < 0 or > 1)
        {
            return Result.Failure<BillingPolicyDto>(
                "Billing policy values are invalid.",
                ErrorCodes.ValidationError);
        }

        try
        {

            await _repository.UpsertPolicyValueAsync(VatRateConfigKey, request.VatRate, cancellationToken);

            return Result.Success(new BillingPolicyDto(request.VatRate));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating billing policy");
            return Result.Failure<BillingPolicyDto>("Unable to update billing policy.", ErrorCodes.InternalServerError);
        }
    }
}
