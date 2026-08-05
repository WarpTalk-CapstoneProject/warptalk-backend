using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Infrastructure.Services;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.Infrastructure.Services;

/// <summary>
/// WT-208. Covers the admin rate-card and pricing-config write paths, which previously had no
/// tests at all — an ordering defect in this area was caught only during code review.
///
/// The focus here is the transaction orchestration: which guards run *before* a transaction is
/// opened, and whether every failure path still releases it. A guard that rejects after
/// BeginTransactionAsync, or a throw that skips the rollback, leaks a connection under load.
/// </summary>
public class UsageRateCardAdminServiceTests
{
    /// <summary>An identity that is present in the service's registered-identity allowlist.</summary>
    private static UpsertUsageRateCardRequest RegisteredRequest(
        decimal unitPrice = 1.643750m,
        decimal? providerUnitCostUsd = 0.0001000000m,
        decimal? markupMultiplier = 2.5m) =>
        new(
            "STT",
            "second",
            "openai",
            "gpt-4o-transcribe",
            null,
            null,
            unitPrice,
            "VND",
            providerUnitCostUsd,
            markupMultiplier,
            true);

    private static UsageRateCardDto InsertedRow(UpsertUsageRateCardRequest request) =>
        new(
            Guid.NewGuid(),
            request.ChargeType,
            request.Unit,
            request.Provider,
            request.Model,
            request.SourceLanguageCode,
            request.TargetLanguageCode,
            request.UnitPrice,
            request.Currency,
            request.ProviderUnitCostUsd,
            request.MarkupMultiplier,
            DateTime.UtcNow,
            null,
            true);

    private static UpdatePricingConfigRequest ValidPricingConfig() =>
        new(
            FxRateUsdVnd: 25_000m,
            CreditValueVnd: 260m,
            MinimumPricePerCreditVnd: 260m,
            MinimumContractPriceVnd: 1_000_000m,
            MinimumContractPriceUsd: 40m,
            SalesUsageWeight: 0.4m,
            SalesMembersWeight: 0.3m,
            SalesLanguagesWeight: 0.2m,
            SalesAiServicesWeight: 0.1m,
            DefaultOverageCapRatio: 0.5m,
            DefaultInvoiceTermsDays: 15m,
            DefaultInvoiceGraceHours: 48m);

    /// <summary>Builds the service over a mock repository that records the call order.</summary>
    private static (UsageRateCardAdminService Service, Mock<IUsageRateCardRepository> Repository, List<string> Calls) CreateService()
    {
        var calls = new List<string>();
        var repository = new Mock<IUsageRateCardRepository>(MockBehavior.Strict);

        repository
            .Setup(r => r.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("begin"))
            .Returns(Task.CompletedTask);
        repository
            .Setup(r => r.CommitTransactionAsync(It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("commit"))
            .Returns(Task.CompletedTask);
        repository
            .Setup(r => r.RollbackTransactionAsync(It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("rollback"))
            .Returns(Task.CompletedTask);

        var service = new UsageRateCardAdminService(
            repository.Object,
            NullLogger<UsageRateCardAdminService>.Instance);

        return (service, repository, calls);
    }

    // ---------------------------------------------------------------------
    // UpsertRateCardAsync — guards that must run before any transaction opens
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("", "second", "openai", "gpt-4o-transcribe")]
    [InlineData("STT", "", "openai", "gpt-4o-transcribe")]
    [InlineData("STT", "second", "", "gpt-4o-transcribe")]
    [InlineData("STT", "second", "openai", "")]
    public async Task UpsertRateCardAsync_BlankIdentityField_IsRejectedBeforeOpeningATransaction(
        string chargeType, string unit, string provider, string model)
    {
        var (service, repository, calls) = CreateService();
        var request = new UpsertUsageRateCardRequest(
            chargeType, unit, provider, model, null, null, 1m, "VND", 0.1m, 2m, true);

        var result = await service.UpsertRateCardAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        calls.Should().BeEmpty("validation must reject the request before a transaction is opened");
        repository.Verify(r => r.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(-1, 0.1, 2)]      // negative unit price
    [InlineData(1, -0.1, 2)]      // negative provider cost
    [InlineData(1, 0.1, -2)]      // negative markup
    public async Task UpsertRateCardAsync_NegativeMoneyValue_IsRejectedBeforeOpeningATransaction(
        double unitPrice, double providerCost, double markup)
    {
        var (service, repository, calls) = CreateService();
        var request = RegisteredRequest(
            (decimal)unitPrice, (decimal)providerCost, (decimal)markup);

        var result = await service.UpsertRateCardAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        calls.Should().BeEmpty();
        repository.Verify(r => r.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpsertRateCardAsync_UnregisteredIdentity_IsRejectedBeforeOpeningATransaction()
    {
        var (service, repository, calls) = CreateService();
        var request = new UpsertUsageRateCardRequest(
            "BOGUS_TEST_NOT_SEEDED", "unit", "test", "test-model", null, null, 1m, "VND", 0.1m, 2m, true);

        var result = await service.UpsertRateCardAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Contain("not registered");
        calls.Should().BeEmpty();
        repository.Verify(r => r.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------------
    // UpsertRateCardAsync — transaction lifecycle
    // ---------------------------------------------------------------------

    [Fact]
    public async Task UpsertRateCardAsync_IdentityAbsentFromDatabase_RollsBackWithoutWriting()
    {
        var (service, repository, calls) = CreateService();
        var request = RegisteredRequest();

        repository
            .Setup(r => r.RateCardIdentityExistsAsync(request, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("exists"))
            .ReturnsAsync(false);

        var result = await service.UpsertRateCardAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        calls.Should().Equal("begin", "exists", "rollback");
        repository.Verify(
            r => r.UpsertRateCardAsync(It.IsAny<UpsertUsageRateCardRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        repository.Verify(r => r.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpsertRateCardAsync_RegisteredAndSeededIdentity_CommitsAndReturnsInsertedRow()
    {
        var (service, repository, calls) = CreateService();
        var request = RegisteredRequest();
        var inserted = InsertedRow(request);

        repository
            .Setup(r => r.RateCardIdentityExistsAsync(request, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("exists"))
            .ReturnsAsync(true);
        repository
            .Setup(r => r.UpsertRateCardAsync(request, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("upsert"))
            .ReturnsAsync(inserted);

        var result = await service.UpsertRateCardAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(inserted);

        // The existence check must be inside the transaction: it is a read the write depends on,
        // so a concurrent supersede between check and insert would otherwise go unnoticed.
        calls.Should().Equal("begin", "exists", "upsert", "commit");
        repository.Verify(r => r.RollbackTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpsertRateCardAsync_RepositoryThrows_RollsBackAndReportsInternalError()
    {
        var (service, repository, calls) = CreateService();
        var request = RegisteredRequest();

        repository
            .Setup(r => r.RateCardIdentityExistsAsync(request, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("exists"))
            .ReturnsAsync(true);
        repository
            .Setup(r => r.UpsertRateCardAsync(request, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("upsert"))
            .ThrowsAsync(new InvalidOperationException("unique index violated"));

        var result = await service.UpsertRateCardAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InternalServerError);
        result.Error.Should().NotContain("unique index violated", "internal details must not leak to admins");
        calls.Should().Equal("begin", "exists", "upsert", "rollback");
        repository.Verify(r => r.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------------
    // GetActiveRateCardsAsync
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetActiveRateCardsAsync_ReturnsRowsFromRepository()
    {
        var (service, repository, _) = CreateService();
        var rows = new List<UsageRateCardDto> { InsertedRow(RegisteredRequest()) };

        repository
            .Setup(r => r.GetActiveRateCardsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows);

        var result = await service.GetActiveRateCardsAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(rows);
    }

    [Fact]
    public async Task GetActiveRateCardsAsync_RepositoryThrows_ReportsInternalError()
    {
        var (service, repository, _) = CreateService();

        repository
            .Setup(r => r.GetActiveRateCardsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("db down"));

        var result = await service.GetActiveRateCardsAsync();

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InternalServerError);
        result.Error.Should().NotContain("db down");
    }

    // ---------------------------------------------------------------------
    // UpdatePricingConfigAsync — credit economics
    // ---------------------------------------------------------------------

    [Fact]
    public async Task UpdatePricingConfigAsync_NonPositiveCreditValue_IsRejectedBeforeOpeningATransaction()
    {
        var (service, repository, calls) = CreateService();
        var request = ValidPricingConfig() with { CreditValueVnd = 0m };

        var result = await service.UpdatePricingConfigAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        calls.Should().BeEmpty();
        repository.Verify(r => r.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdatePricingConfigAsync_OverageCapRatioAboveOne_IsRejected()
    {
        var (service, _, calls) = CreateService();
        var request = ValidPricingConfig() with { DefaultOverageCapRatio = 1.5m };

        var result = await service.UpdatePricingConfigAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdatePricingConfigAsync_AllSalesWeightsZero_IsRejected()
    {
        var (service, _, calls) = CreateService();

        // Each weight passes its own non-negative check; only the total rules this out. Without
        // the total guard the sales formula would divide by zero at pricing time.
        var request = ValidPricingConfig() with
        {
            SalesUsageWeight = 0m,
            SalesMembersWeight = 0m,
            SalesLanguagesWeight = 0m,
            SalesAiServicesWeight = 0m,
        };

        var result = await service.UpdatePricingConfigAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Contain("weight");
        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdatePricingConfigAsync_ValidRequest_WritesEveryKeyInOneTransaction()
    {
        var (service, repository, calls) = CreateService();
        var request = ValidPricingConfig();
        var writtenKeys = new List<string>();

        repository
            .Setup(r => r.UpsertPricingConfigValueAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Callback<string, decimal, CancellationToken>((key, _, _) => writtenKeys.Add(key))
            .Returns(Task.CompletedTask);

        var result = await service.UpdatePricingConfigAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.CreditValueVnd.Should().Be(request.CreditValueVnd);
        result.Value.FxRateUsdVnd.Should().Be(request.FxRateUsdVnd);

        // Every configured key is written, each exactly once, inside a single transaction.
        writtenKeys.Should().HaveCount(12).And.OnlyHaveUniqueItems();
        calls.Should().Equal("begin", "commit");
    }

    [Fact]
    public async Task UpdatePricingConfigAsync_WriteThrowsMidway_RollsBackTheWholeBatch()
    {
        var (service, repository, calls) = CreateService();
        var request = ValidPricingConfig();
        var written = 0;

        repository
            .Setup(r => r.UpsertPricingConfigValueAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                written++;
                return written < 3
                    ? Task.CompletedTask
                    : Task.FromException(new InvalidOperationException("connection reset"));
            });

        var result = await service.UpdatePricingConfigAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InternalServerError);

        // A partially-applied pricing config would price usage with a mix of old and new
        // values, so the batch must roll back as a unit rather than commit what it managed.
        calls.Should().Equal("begin", "rollback");
        repository.Verify(r => r.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------------
    // GetPricingConfigAsync
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetPricingConfigAsync_UnsetKeys_FallBackToTheSuppliedDefaults()
    {
        var (service, repository, _) = CreateService();
        var requestedDefaults = new List<decimal>();

        // Echo each caller-supplied default back, standing in for a key that has no stored row.
        repository
            .Setup(r => r.ReadPricingConfigValueAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Returns<string, decimal, CancellationToken>((_, defaultValue, _) =>
            {
                requestedDefaults.Add(defaultValue);
                return Task.FromResult(defaultValue);
            });

        var result = await service.GetPricingConfigAsync();

        result.IsSuccess.Should().BeTrue();
        requestedDefaults.Should().HaveCount(12);

        var config = result.Value!;
        config.FxRateUsdVnd.Should().BePositive();
        config.CreditValueVnd.Should().BePositive();
        config.Formula.Should().NotBeNullOrWhiteSpace();
        config.ResolverKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetPricingConfigAsync_RepositoryThrows_ReportsInternalError()
    {
        var (service, repository, _) = CreateService();

        repository
            .Setup(r => r.ReadPricingConfigValueAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("db down"));

        var result = await service.GetPricingConfigAsync();

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InternalServerError);
    }

    // ---------------------------------------------------------------------
    // DeactivateRateCardAsync — retiring a published rate
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DeactivateRateCardAsync_EmptyId_IsRejectedBeforeOpeningATransaction()
    {
        var (service, repository, calls) = CreateService();

        var result = await service.DeactivateRateCardAsync(Guid.Empty);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        calls.Should().BeEmpty();
        repository.Verify(r => r.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeactivateRateCardAsync_UnknownId_RollsBackAndReportsNotFound()
    {
        var (service, repository, calls) = CreateService();
        var id = Guid.NewGuid();

        repository
            .Setup(r => r.DeactivateRateCardAsync(id, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("deactivate"))
            .ReturnsAsync((UsageRateCardDto?)null);

        var result = await service.DeactivateRateCardAsync(id);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        calls.Should().Equal("begin", "deactivate", "rollback");
        repository.Verify(r => r.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeactivateRateCardAsync_KnownId_CommitsAndReturnsTheRetiredRow()
    {
        var (service, repository, calls) = CreateService();
        var retired = InsertedRow(RegisteredRequest()) with
        {
            IsActive = false,
            EffectiveTo = DateTime.UtcNow,
        };

        repository
            .Setup(r => r.DeactivateRateCardAsync(retired.Id, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("deactivate"))
            .ReturnsAsync(retired);

        var result = await service.DeactivateRateCardAsync(retired.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.IsActive.Should().BeFalse();

        // Closing the row as well as clearing IsActive is what stops it coming back:
        // GetActiveRateCardsAsync now requires both, and the next upsert supersedes every
        // row still open for that identity.
        result.Value.EffectiveTo.Should().NotBeNull();
        calls.Should().Equal("begin", "deactivate", "commit");
        repository.Verify(r => r.RollbackTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeactivateRateCardAsync_RepositoryThrows_RollsBackAndReportsInternalError()
    {
        var (service, repository, calls) = CreateService();
        var id = Guid.NewGuid();

        repository
            .Setup(r => r.DeactivateRateCardAsync(id, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("deactivate"))
            .ThrowsAsync(new InvalidOperationException("deadlock detected"));

        var result = await service.DeactivateRateCardAsync(id);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InternalServerError);
        result.Error.Should().NotContain("deadlock detected");
        calls.Should().Equal("begin", "deactivate", "rollback");
    }

    // ---------------------------------------------------------------------
    // PreviewRateCardAsync — pricing a proposed rate without publishing it
    // ---------------------------------------------------------------------

    [Fact]
    public async Task PreviewRateCardAsync_WithoutOverrides_UsesTheStoredPricingConfig()
    {
        var (service, repository, calls) = CreateService();

        repository
            .Setup(r => r.ReadPricingConfigValueAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Returns<string, decimal, CancellationToken>((key, _, _) => Task.FromResult(
                key == "fx_rate_usd_vnd" ? 26_300m : 4m));

        var result = await service.PreviewRateCardAsync(
            new RateCardPreviewRequest(ProviderUnitCostUsd: 0.0001000000m, MarkupMultiplier: 2.5m));

        result.IsSuccess.Should().BeTrue();
        result.Value!.UnitPriceCredits.Should().Be(1.643750m);
        result.Value.FxRateUsdVnd.Should().Be(26_300m);
        result.Value.CreditValueVnd.Should().Be(4m);
        result.Value.Formula.Should().NotBeNullOrWhiteSpace();

        // A preview must never write, so it must never open a transaction either.
        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PreviewRateCardAsync_WithOverrides_DoesNotReadTheStoredConfig()
    {
        var (service, repository, _) = CreateService();

        var result = await service.PreviewRateCardAsync(new RateCardPreviewRequest(
            ProviderUnitCostUsd: 0.0001000000m,
            MarkupMultiplier: 2.5m,
            Quantity: 1m,
            FxRateUsdVnd: 26_300m,
            CreditValueVnd: 4m));

        result.IsSuccess.Should().BeTrue();
        result.Value!.UnitPriceCredits.Should().Be(1.643750m);

        // Both economics values were supplied, so nothing needs loading — this is what lets
        // an admin preview a proposed FX/credit-value change before saving it.
        repository.Verify(
            r => r.ReadPricingConfigValueAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PreviewRateCardAsync_ImpossibleInputs_ReportValidationRatherThanServerError()
    {
        var (service, _, _) = CreateService();

        var result = await service.PreviewRateCardAsync(new RateCardPreviewRequest(
            ProviderUnitCostUsd: 0.01m,
            MarkupMultiplier: 2.5m,
            Quantity: 1m,
            FxRateUsdVnd: 26_300m,
            CreditValueVnd: 0m));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }

    [Fact]
    public async Task PreviewRateCardAsync_ConfigReadThrows_ReportsInternalError()
    {
        var (service, repository, _) = CreateService();

        repository
            .Setup(r => r.ReadPricingConfigValueAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("db down"));

        var result = await service.PreviewRateCardAsync(
            new RateCardPreviewRequest(0.0001m, 2.5m));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InternalServerError);
    }
}
