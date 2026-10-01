using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// WT-878: converting a sales lead into a contract cancelled the workspace's subscription and then
/// created the contract row. A cancel keeps a paid row live until its period ends (and always did
/// for paid rows), so the create failed with "already active" for every paying workspace. The
/// conversion now retires the live row and creates the contract in one commit.
/// </summary>
public class SalesInquiryContractConversionTests
{
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly List<Subscription> _subscriptions = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IStripePaymentService> _stripe = new();
    private readonly SalesInquiry _inquiry;
    private readonly Plan _proPlan;
    private readonly Plan _enterprisePlan;
    private readonly SalesInquiryService _service;

    public SalesInquiryContractConversionTests()
    {
        _proPlan = new Plan { Id = Guid.NewGuid(), Slug = "pro", Name = "Pro", Price = 500_000m, CreditsPerCycle = 100_000, IsActive = true };
        _enterprisePlan = new Plan
        {
            Id = Guid.NewGuid(),
            Slug = SubscriptionConstants.PlanSlugs.Enterprise,
            Name = "Enterprise",
            Price = 1_900_000m,
            CreditsPerCycle = 700_000,
            OverageCapCredits = 105_000,
            OveragePricePerCredit = 4m,
            InvoiceTermsDays = 15,
            IsActive = true,
        };
        var plans = new List<Plan> { _proPlan, _enterprisePlan };
        _inquiry = new SalesInquiry { Id = Guid.NewGuid(), WorkEmail = "buyer@example.com" };

        var planRepo = new Mock<IPlanRepository>();
        planRepo
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<Plan, bool>> p, CancellationToken _) => plans.FirstOrDefault(p.Compile()));
        planRepo
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => plans.FirstOrDefault(p => p.Id == id));

        var subRepo = new Mock<ISubscriptionRepository>();
        subRepo
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<Subscription, bool>> p, CancellationToken _) => _subscriptions.FirstOrDefault(p.Compile()));
        subRepo
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<Subscription, bool>> p, string _, CancellationToken _) => _subscriptions.Where(p.Compile()).ToList());
        subRepo
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<Subscription, bool>> p, CancellationToken _) => _subscriptions.Where(p.Compile()).ToList());
        subRepo
            .Setup(r => r.GetActiveByWorkspaceIdAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid ws, bool _, bool _, CancellationToken _) => _subscriptions.FirstOrDefault(s => s.WorkspaceId == ws && s.IsActive));
        subRepo
            .Setup(r => r.AddAsync(It.IsAny<Subscription>(), It.IsAny<CancellationToken>()))
            .Callback<Subscription, CancellationToken>((s, _) => _subscriptions.Add(s))
            .Returns(Task.CompletedTask);

        var inquiries = new Mock<ISalesInquiryRepository>();
        inquiries
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => id == _inquiry.Id ? _inquiry : null);

        _unitOfWork.Setup(u => u.Plans).Returns(planRepo.Object);
        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(subRepo.Object);
        _unitOfWork.Setup(u => u.SalesInquiryRepository).Returns(inquiries.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var pricing = new Mock<IUsageRateCardAdminService>();
        pricing
            .Setup(s => s.GetPricingConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new PricingConfigDto(
                FxRateUsdVnd: 26300m, CreditValueVnd: 4m, MinimumPricePerCreditVnd: 2.60m,
                MinimumContractPriceVnd: 15000m, MinimumContractPriceUsd: 0.50m,
                SalesUsageWeight: 0.45m, SalesMembersWeight: 0.15m, SalesLanguagesWeight: 0.15m, SalesAiServicesWeight: 0.25m,
                DefaultOverageCapRatio: 0.15m, DefaultInvoiceTermsDays: 15m, DefaultInvoiceGraceHours: 360m,
                Formula: "", ResolverKey: "")));

        var subscriptions = new SubscriptionService(
            _unitOfWork.Object,
            new Mock<ILogger<SubscriptionService>>().Object,
            new Mock<IBillingMessagePublisher>().Object,
            _stripe.Object,
            pricing.Object,
            new Mock<IWorkspaceClient>().Object,
            new Mock<IAiServiceStateStore>().Object);

        _service = new SalesInquiryService(_unitOfWork.Object, subscriptions);
    }

    private Subscription PaidStripeRow() => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = _workspaceId,
        UserId = Guid.NewGuid(),
        PlanId = _proPlan.Id,
        Status = SubscriptionConstants.SubscriptionStatuses.Active,
        IsActive = true,
        AutoRenew = true,
        RenewalMode = SubscriptionConstants.RenewalModes.Stripe,
        StripeSubscriptionId = "sub_test_paid",
        CreditsRemaining = 42_000,
        CurrentPeriodStart = DateTime.UtcNow.AddDays(-10),
        CurrentPeriodEnd = DateTime.UtcNow.AddDays(20),
    };

    private ConvertSalesInquiryToContractRequest Request() => new(
        _workspaceId,
        _enterprisePlan.Id,
        new UpdateSubscriptionContractTermsRequest(
            CreditsPerCycleOverride: 710_000,
            ContractPriceVnd: 1_900_000m,
            OverageCapCreditsOverride: 105_000,
            OveragePricePerCreditOverride: 4m,
            InvoiceTermsDaysOverride: 15,
            BillingContactEmail: "billing@example.com"));

    [Fact]
    public async Task Converting_a_workspace_with_a_paid_live_row_succeeds_and_leaves_exactly_one_live_row()
    {
        var paid = PaidStripeRow();
        _subscriptions.Add(paid);
        _stripe
            .Setup(s => s.SetPlanSubscriptionCancelAtPeriodEndAsync("sub_test_paid", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("active"));

        var result = await _service.ConvertSalesInquiryToContractAsync(_inquiry.Id, Request());

        result.IsSuccess.Should().BeTrue(result.Error);
        var live = _subscriptions.Where(s => s.WorkspaceId == _workspaceId && s.IsActive).ToList();
        live.Should().ContainSingle();
        live[0].Id.Should().NotBe(paid.Id);
        live[0].PlanId.Should().Be(_enterprisePlan.Id);
        _inquiry.SubscriptionId.Should().Be(live[0].Id);

        paid.IsActive.Should().BeFalse();
        paid.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Cancelled);
        paid.AutoRenew.Should().BeFalse();
        paid.CancelledAt.Should().NotBeNull();
        paid.CreditsRemaining.Should().Be(42_000, "the balance stays on the row for the frozen-credit sweep, not dropped");
        _stripe.Verify(s => s.SetPlanSubscriptionCancelAtPeriodEndAsync("sub_test_paid", true, It.IsAny<CancellationToken>()), Times.Once,
            "the card must stop being charged for the plan the contract replaces");
    }

    [Fact]
    public async Task A_Stripe_failure_fails_the_conversion_and_changes_nothing()
    {
        var paid = PaidStripeRow();
        _subscriptions.Add(paid);
        _stripe
            .Setup(s => s.SetPlanSubscriptionCancelAtPeriodEndAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<string>("stripe down", ErrorCodes.BillingExternalServiceError));

        var result = await _service.ConvertSalesInquiryToContractAsync(_inquiry.Id, Request());

        result.IsSuccess.Should().BeFalse();
        paid.IsActive.Should().BeTrue();
        paid.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Active);
        _subscriptions.Should().ContainSingle("no contract row was created");
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_direct_create_still_refuses_a_workspace_that_has_a_live_row()
    {
        _subscriptions.Add(PaidStripeRow());

        var subscriptions = new SubscriptionService(
            _unitOfWork.Object,
            new Mock<ILogger<SubscriptionService>>().Object,
            new Mock<IBillingMessagePublisher>().Object,
            _stripe.Object,
            new Mock<IUsageRateCardAdminService>().Object,
            new Mock<IWorkspaceClient>().Object,
            new Mock<IAiServiceStateStore>().Object);

        var result = await subscriptions.CreateWorkspaceContractSubscriptionAsync(
            new CreateWorkspaceContractSubscriptionRequest(_workspaceId, _enterprisePlan.Id, Request().ContractTerms));

        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionAlreadyActive);
    }
}
