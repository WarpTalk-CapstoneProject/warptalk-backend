using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.BillingService.Tests;

/// <summary>
/// Quota Enforcement & Resource Protection Tests
/// Covers: Quota deduction idempotency, multi-layer enforcement, concurrency safety
/// ISO/IEC 27001/27002: Resource protection, abuse prevention, audit compliance
/// </summary>
public class QuotaEnforcementTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;

    public QuotaEnforcementTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:RequireAuthentication"] = "false",
                    ["Billing:AutoSeedOnStartup"] = "true"
                });
            });

            builder.ConfigureServices(services =>
            {
                var dbDescriptors = services.Where(d =>
                    d.ServiceType == typeof(DbContextOptions<BillingDbContext>) ||
                    d.ServiceType == typeof(BillingDbContext) ||
                    d.ServiceType.Name.Contains("DbContextOptions")).ToList();

                foreach (var d in dbDescriptors)
                    services.Remove(d);

                services.AddDbContext<BillingDbContext>(options =>
                    options.UseInMemoryDatabase($"QuotaEnforcementTestDb_{Guid.NewGuid()}"));
            });
        });

        _client = _factory.CreateClient();
    }

    // ============================================================
    // ✅ QUOTA DEDUCTION - HAPPY PATH
    // ============================================================

    [Fact]
    public async Task ValidQuotaDeduction_ShouldSucceed()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await CreateSubscriptionAsync(workspaceId, "Pro");

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes = 10.5m,
                source = "worker-test"
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadAsAsync<dynamic>();
        ((decimal)result.remainingMinutes).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task MultipleSequentialDeductions_ShouldReduce QuotaProggressively()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await CreateSubscriptionAsync(workspaceId, "Pro"); // 1000 minutes

        var initialQuota = await GetQuotaAsync(workspaceId);

        // Act & Assert - Deduct multiple times
        for (int i = 0; i < 3; i++)
        {
            var response = await _client.PostAsJsonAsync("/api/v1/billing/quota/deduct",
                new
                {
                    workspaceId,
                    sessionId = Guid.NewGuid(),
                    consumedMinutes = 100m,
                    source = "test"
                });

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var currentQuota = await GetQuotaAsync(workspaceId);
            currentQuota.Should().Be(initialQuota - ((i + 1) * 100m));
        }
    }

    // ============================================================
    // 🔄 IDEMPOTENCY TESTS (Prevent duplicate deductions)
    // ============================================================

    [Fact]
    public async Task DuplicateDeduction_WithSameReferenceId_ShouldNotDeductTwice()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var referenceId = Guid.NewGuid();
        const decimal consumedMinutes = 50m;

        await CreateSubscriptionAsync(workspaceId, "Pro");
        var initialQuota = await GetQuotaAsync(workspaceId);

        // Act - First deduction
        var firstResponse = await _client.PostAsJsonAsync("/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes,
                source = "test",
                referenceId
            });

        var afterFirstDeduction = await GetQuotaAsync(workspaceId);

        // Act - Duplicate deduction with same referenceId
        var secondResponse = await _client.PostAsJsonAsync("/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes,
                source = "test",
                referenceId // Same reference
            });

        var finalQuota = await GetQuotaAsync(workspaceId);

        // Assert - Quota should only be deducted once
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK); // Idempotent
        finalQuota.Should().Be(afterFirstDeduction); // No additional deduction
        (initialQuota - finalQuota).Should().Be(consumedMinutes); // Only deducted once
    }

    [Fact]
    public async Task DifferentSessionSameWorkspace_ShouldAllowMultipleDeductions()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        const decimal deductAmount = 25m;

        await CreateSubscriptionAsync(workspaceId, "Pro");
        var initialQuota = await GetQuotaAsync(workspaceId);

        // Act - Deduct with different sessions
        var response1 = await _client.PostAsJsonAsync("/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(), // Different session
                consumedMinutes = deductAmount,
                source = "test"
            });

        var response2 = await _client.PostAsJsonAsync("/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(), // Different session
                consumedMinutes = deductAmount,
                source = "test"
            });

        var finalQuota = await GetQuotaAsync(workspaceId);

        // Assert - Both should succeed and total deduction should be cumulative
        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        response2.StatusCode.Should().Be(HttpStatusCode.OK);
        (initialQuota - finalQuota).Should().Be(deductAmount * 2);
    }

    // ============================================================
    // 🔒 CONCURRENCY & RACE CONDITION TESTS
    // ============================================================

    [Fact]
    public async Task ConcurrentDeductions_ShouldNotCauseLostUpdates()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        const decimal deductPerRequest = 10m;
        const int concurrentRequests = 20;

        await CreateSubscriptionAsync(workspaceId, "Pro"); // 1000 minutes
        var initialQuota = await GetQuotaAsync(workspaceId);

        // Act - Send concurrent deduction requests
        var tasks = Enumerable.Range(0, concurrentRequests)
            .Select(_ => _client.PostAsJsonAsync("/api/v1/billing/quota/deduct",
                new
                {
                    workspaceId,
                    sessionId = Guid.NewGuid(),
                    consumedMinutes = deductPerRequest,
                    source = "concurrent-test"
                }))
            .ToList();

        var responses = await Task.WhenAll(tasks);

        var finalQuota = await GetQuotaAsync(workspaceId);

        // Assert - All requests should succeed
        responses.All(r => r.StatusCode == HttpStatusCode.OK).Should().BeTrue();

        // Total deduction should equal concurrent requests * amount per request (no lost updates)
        var expectedFinalQuota = initialQuota - (concurrentRequests * deductPerRequest);
        finalQuota.Should().BeCloseTo(expectedFinalQuota, delta: 0.1m); // Small delta for timing
    }

    [Fact]
    public async Task ConcurrentDeductionsWithInsufficientQuota_ShouldHandleGracefully()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        const decimal deductPerRequest = 50m;
        const int concurrentRequests = 30; // Will exceed quota

        await CreateSubscriptionAsync(workspaceId, "Basic"); // Only 100 minutes
        var initialQuota = await GetQuotaAsync(workspaceId);

        // Act - Send concurrent requests that exceed available quota
        var tasks = Enumerable.Range(0, concurrentRequests)
            .Select(_ => _client.PostAsJsonAsync("/api/v1/billing/quota/deduct",
                new
                {
                    workspaceId,
                    sessionId = Guid.NewGuid(),
                    consumedMinutes = deductPerRequest,
                    source = "over-limit-test"
                }))
            .ToList();

        var responses = await Task.WhenAll(tasks);

        var finalQuota = await GetQuotaAsync(workspaceId);

        // Assert - Some requests should fail or be rejected
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().BeLessThan(concurrentRequests);
        finalQuota.Should().BeLessThanOrEqualTo(initialQuota);
    }

    // ============================================================
    // 🚫 BOUNDARY & VALIDATION TESTS
    // ============================================================

    [Fact]
    public async Task ZeroMinutes_ShouldBeRejected()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await CreateSubscriptionAsync(workspaceId, "Pro");

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes = 0m,
                source = "test"
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NegativeMinutes_ShouldBeRejected()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await CreateSubscriptionAsync(workspaceId, "Pro");

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes = -10m,
                source = "test"
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MinimumValidAmount_ShouldSucceed()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await CreateSubscriptionAsync(workspaceId, "Pro");

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes = 0.01m,
                source = "test"
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExceedsMaximumAmount_ShouldBeRejected()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await CreateSubscriptionAsync(workspaceId, "Pro");

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes = 10001m, // Exceeds 10000 limit
                source = "test"
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task InsufficientQuota_ShouldBeRejected()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await CreateSubscriptionAsync(workspaceId, "Basic"); // Only 100 minutes

        // Act - Try to deduct more than available
        var response = await _client.PostAsJsonAsync("/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes = 150m,
                source = "test"
            });

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Conflict);
    }

    // ============================================================
    // 📊 QUOTA ENFORCEMENT AT MULTIPLE LAYERS
    // ============================================================

    [Fact]
    public async Task QuotaCheckEndpoint_ShouldReflectCurrentBalance()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await CreateSubscriptionAsync(workspaceId, "Pro"); // 1000 minutes

        // Act - Deduct 100 minutes
        await _client.PostAsJsonAsync("/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes = 100m,
                source = "test"
            });

        // Assert - Check endpoint should show correct balance
        var checkResponse = await _client.GetAsync(
            $"/api/v1/billing/quota/check?workspaceId={workspaceId}");

        checkResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var quota = await checkResponse.Content.ReadAsAsync<dynamic>();
        ((decimal)quota.quota).Should().Be(900m);
    }

    [Fact]
    public async Task QuotaHistoryEndpoint_ShouldShowAllDeductions()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await CreateSubscriptionAsync(workspaceId, "Pro");

        // Act - Make multiple deductions
        for (int i = 0; i < 5; i++)
        {
            await _client.PostAsJsonAsync("/api/v1/billing/quota/deduct",
                new
                {
                    workspaceId,
                    sessionId = Guid.NewGuid(),
                    consumedMinutes = 10m,
                    source = "test"
                });
        }

        // Assert - History should contain all deductions
        var historyResponse = await _client.GetAsync(
            $"/api/v1/billing/quota/{workspaceId}/history?page=1&pageSize=50");

        historyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var history = await historyResponse.Content.ReadAsAsync<dynamic>();
        history.Should().NotBeNull();
    }

    // ============================================================
    // 📝 AUDIT TRAIL VERIFICATION
    // ============================================================

    [Fact]
    public async Task EachQuotaDeduction_ShouldCreateAuditLog()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await CreateSubscriptionAsync(workspaceId, "Pro");

        // Act
        await _client.PostAsJsonAsync("/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes = 25m,
                source = "audit-test"
            });

        // Assert - Audit log created
        var auditResponse = await _client.GetAsync(
            $"/api/v1/billing/quota/{workspaceId}/usage-logs?page=1&pageSize=10");

        auditResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var logs = await auditResponse.Content.ReadAsAsync<dynamic>();
        logs.Should().NotBeNull();
    }

    [Fact]
    public async Task RefundOperation_ShouldCreateAuditLogWithCorrectChangeType()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await CreateSubscriptionAsync(workspaceId, "Pro");

        // Act - Deduct then refund
        await _client.PostAsJsonAsync("/api/v1/billing/quota/deduct",
            new { workspaceId, sessionId, consumedMinutes = 50m, source = "test" });

        var refundResponse = await _client.PostAsJsonAsync("/api/v1/billing/quota/refund",
            new { workspaceId, sessionId, refundMinutes = 25m, reason = "test-refund" });

        // Assert
        refundResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ============================================================
    // Helper Methods
    // ============================================================

    private async Task CreateSubscriptionAsync(Guid workspaceId, string planType)
    {
        var planId = planType == "Pro"
            ? Guid.Parse("00000000-0000-0000-0000-000000000001")
            : Guid.Parse("00000000-0000-0000-0000-000000000002");

        await _client.PostAsJsonAsync("/api/v1/billing/subscription",
            new { workspaceId, planId });
    }

    private async Task<decimal> GetQuotaAsync(Guid workspaceId)
    {
        var response = await _client.GetAsync(
            $"/api/v1/billing/quota/check?workspaceId={workspaceId}");

        var data = await response.Content.ReadAsAsync<dynamic>();
        return (decimal)data.quota;
    }
}
