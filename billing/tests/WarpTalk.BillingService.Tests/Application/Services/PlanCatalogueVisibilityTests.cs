using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// BR-74 — who sees a deactivated plan.
///
/// `GetActivePlansAsync` filtered on `DeletedAt` alone despite its name, so a plan an
/// administrator had switched off stayed selectable for new purchases on the landing page and in
/// every checkout flow. `SubscriptionService` already refuses to create a subscription against an
/// inactive plan, so the end state was a customer choosing a plan and being told no at the till.
///
/// The obvious fix — add `&& p.IsActive` and stop — breaks plan management. The admin page lists
/// plans through the same call and toggles IsActive through the edit form, so filtering there
/// would make deactivation a one-way door: the plan disappears from the only list that could turn
/// it back on. Hence two reads, and these tests hold them apart.
/// </summary>
public class PlanCatalogueVisibilityTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IPlanRepository> _plans = new();
    private readonly PlanService _sut;

    public PlanCatalogueVisibilityTests()
    {
        _unitOfWork.Setup(u => u.Plans).Returns(_plans.Object);
        _sut = new PlanService(
            _unitOfWork.Object,
            Mock.Of<ILogger<PlanService>>(),
            Mock.Of<IBillingMessagePublisher>(),
            Mock.Of<IUsageRateCardAdminService>());
    }

    private void Given(params Plan[] plans)
    {
        _plans
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<Plan, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plans.ToList());
    }

    private static Plan Plan(string name, bool isActive) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Slug = name.ToLowerInvariant(),
        IsActive = isActive,
    };

    [Fact]
    public async Task TheCustomerCatalogue_HidesADeactivatedPlan()
    {
        Given(Plan("Enterprise", isActive: true), Plan("Legacy Startup", isActive: false));

        var result = await _sut.GetActivePlansAsync();

        result.Value!.Select(p => p.Name).Should().ContainSingle().Which.Should().Be("Enterprise");
    }

    [Fact]
    public async Task TheAdminCatalogue_KeepsADeactivatedPlanVisible()
    {
        // Without this the admin cannot re-activate what they just switched off.
        Given(Plan("Enterprise", isActive: true), Plan("Legacy Startup", isActive: false));

        var result = await _sut.GetAllPlansAsync();

        result.Value!.Select(p => p.Name).Should().BeEquivalentTo("Enterprise", "Legacy Startup");
    }

    [Fact]
    public async Task AnEmptyCatalogue_IsSeededOnce()
    {
        Given();

        var result = await _sut.GetActivePlansAsync();

        result.Value.Should().NotBeEmpty();
        _plans.Verify(r => r.AddAsync(It.IsAny<Plan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ACatalogueWhereEveryPlanIsDeactivated_IsNotReseeded()
    {
        // The trap in this change. Seeding keyed on "no ACTIVE plans" rather than "no plans"
        // would mint a fresh Enterprise plan on every read once an administrator took the product
        // off sale — silently undoing the decision, and once per request.
        Given(Plan("Enterprise", isActive: false));

        var result = await _sut.GetActivePlansAsync();

        result.Value.Should().BeEmpty();
        _plans.Verify(r => r.AddAsync(It.IsAny<Plan>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
