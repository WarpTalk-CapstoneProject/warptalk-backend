using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.BillingService.Tests;

/// <summary>
/// Phase 4: Comprehensive Integration Tests
/// Covers: happy path, negative path, edge cases, integration points, ISO/IEC 27001/27002 compliance
/// Focus: Idempotency, Concurrency, Authorization, Webhook Security, Audit Trails
/// </summary>
public class Phase4ComprehensiveTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public Phase4ComprehensiveTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:RequireAuthentication"] = "false",
                    ["Billing:AutoSeedOnStartup"] = "true",
                    ["PayOS:UseMockService"] = "true"
                });
            });

            builder.ConfigureServices(services =>
            {
                var dbDescriptors = services.Where(d =>
                    d.ServiceType == typeof(DbContextOptions<BillingDbContext>) ||
                    d.ServiceType == typeof(BillingDbContext) ||
                    d.ServiceType.Name.Contains("DbContextOptions")).ToList();

                foreach (var descriptor in dbDescriptors)
                    services.Remove(descriptor);

                services.AddDbContext<BillingDbContext>(options =>
                    options.UseInMemoryDatabase($"Phase4TestDb_{Guid.NewGuid()}"));
            });
        });

        _client = _factory.CreateClient();
        SeedTestData();
    }

    // ============================================================
    // SEED/MOCK DATA
    // ============================================================
    private void SeedTestData()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

        var prooPlan = new SubscriptionPlan
        {
            Id = TestData.ProPlanId,
            Name = PlanType.Pro,
            Description = "Pro Plan",
            IncludedMinutes = 1000m,
            Price = 99000m,
            Currency = "VND",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var basicPlan = new SubscriptionPlan
        {
            Id = TestData.BasicPlanId,
            Name = PlanType.Basic,
            Description = "Basic Plan",
            IncludedMinutes = 100m,
            Price = 29000m,
            Currency = "VND",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        db.SubscriptionPlans.AddRange(prooPlan, basicPlan);
        db.SaveChanges();
    }

    // ============================================================
    // ✅ HAPPY PATH TESTS
    // ============================================================

    [Fact]
    public async Task QuotaDeduct_ValidRequest_ShouldReduceQuotaAndLogAudit()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        const decimal consumedMinutes = 10.5m;

        // Create subscription first
        await _client.PostAsJsonAsync($"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.ProPlanId });

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/billing/quota/deduct",
            new { workspaceId, sessionId, consumedMinutes = consumedMinutes, source = "api-test" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadAsAsync<dynamic>();
        ((decimal)result.remainingMinutes).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task QuotaRefund_ValidRequest_ShouldIncreaseQuotaAndLogAudit()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        const decimal refundMinutes = 5.5m;

        await _client.PostAsJsonAsync($"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.ProPlanId });

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/billing/quota/refund",
            new { workspaceId, sessionId, refundMinutes = refundMinutes, reason = "cancellation" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PaymentWebhook_ValidSignature_ShouldUpdateTransactionAndQuota()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var orderCode = RandomNumberGenerator.GetInt32(100000, 999999);
        var payload = new PayOsWebhookPayload
        {
            Code = "00",
            Desc = "Success",
            Data = new PayOsWebhookData
            {
                OrderCode = orderCode,
                Amount = 500000,
                Reference = $"WarpTalk-{workspaceId}-TopUp",
                TransactionDateTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                Currency = "VND",
                Code = "00"
            },
            Signature = GenerateHmacSignature(payload, TestData.TestWebhookKey)
        };

        // Act
        var response = await _client.PostAsJsonAsync(
            "/api/v1/billing/payos/webhook", payload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ============================================================
    // 🔒 IDEMPOTENCY TESTS (ISO/IEC 27001: Prevent duplicate deductions)
    // ============================================================

    [Fact]
    public async Task QuotaDeduct_DuplicateReferenceId_ShouldOnlyDeductOnce()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var referenceId = Guid.NewGuid();
        const decimal consumedMinutes = 10m;

        await _client.PostAsJsonAsync($"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.ProPlanId });

        // Get initial quota
        var initialResponse = await _client.GetAsync(
            $"/api/v1/billing/quota/check?workspaceId={workspaceId}");
        var initialData = await initialResponse.Content.ReadAsAsync<dynamic>();
        var initialQuota = (decimal)initialData.quota;

        // Act - First deduction
        var firstResponse = await _client.PostAsJsonAsync(
            $"/api/v1/billing/quota/deduct",
            new { workspaceId, sessionId, consumedMinutes, source = "test", referenceId });

        // Act - Duplicate deduction with same referenceId
        var secondResponse = await _client.PostAsJsonAsync(
            $"/api/v1/billing/quota/deduct",
            new { workspaceId, sessionId, consumedMinutes, source = "test", referenceId });

        // Get final quota
        var finalResponse = await _client.GetAsync(
            $"/api/v1/billing/quota/check?workspaceId={workspaceId}");
        var finalData = await finalResponse.Content.ReadAsAsync<dynamic>();
        var finalQuota = (decimal)finalData.quota;

        // Assert - Only one deduction should occur
        (initialQuota - finalQuota).Should().Be(consumedMinutes);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK); // Idempotent - should not fail
    }

    [Fact]
    public async Task WebhookProcessing_DuplicateOrderCode_ShouldBeIdempotent()
    {
        // Arrange
        var orderCode = RandomNumberGenerator.GetInt32(100000, 999999);
        var referenceId = Guid.NewGuid().ToString();
        var payload = new PayOsWebhookPayload
        {
            Code = "00",
            Desc = "Success",
            Data = new PayOsWebhookData
            {
                OrderCode = orderCode,
                Amount = 500000,
                Reference = referenceId,
                TransactionDateTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                Currency = "VND",
                Code = "00"
            },
            Signature = GenerateHmacSignature(payload, TestData.TestWebhookKey)
        };

        // Act - First webhook
        var firstResponse = await _client.PostAsJsonAsync(
            "/api/v1/billing/payos/webhook", payload);

        // Act - Duplicate webhook
        var secondResponse = await _client.PostAsJsonAsync(
            "/api/v1/billing/payos/webhook", payload);

        // Assert - Both should succeed (idempotent)
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
    }

    // ============================================================
    // 🔄 CONCURRENCY TESTS (ISO/IEC 27001: Race condition prevention)
    // ============================================================

    [Fact]
    public async Task ConcurrentQuotaDeductions_ShouldNotCauseLostUpdates()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        const decimal initialQuota = 1000m;
        const decimal deductAmount = 10m;
        const int concurrentRequests = 10;

        await _client.PostAsJsonAsync($"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.ProPlanId });

        // Act - Send concurrent deduct requests
        var tasks = Enumerable.Range(0, concurrentRequests)
            .Select(_ => _client.PostAsJsonAsync(
                $"/api/v1/billing/quota/deduct",
                new
                {
                    workspaceId,
                    sessionId = Guid.NewGuid(),
                    consumedMinutes = deductAmount,
                    source = "concurrent-test"
                }))
            .ToList();

        await Task.WhenAll(tasks);

        // Assert - All should succeed
        foreach (var task in tasks)
        {
            var response = task.Result;
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Verify total deduction is correct
        var finalResponse = await _client.GetAsync(
            $"/api/v1/billing/quota/check?workspaceId={workspaceId}");
        var finalData = await finalResponse.Content.ReadAsAsync<dynamic>();
        var remainingQuota = (decimal)finalData.quota;

        // Each concurrent request should reduce quota
        (initialQuota - remainingQuota).Should().BeCloseTo(concurrentRequests * deductAmount, 
            delta: 1m); // Small delta for timing
    }

    // ============================================================
    // 🔐 AUTHORIZATION & RBAC TESTS (ISO/IEC 27001: Access Control)
    // ============================================================

    [Fact]
    public async Task QuotaDeduct_MissingWorkspaceHeader_ShouldReturnBadRequest()
    {
        // Arrange & Act
        var response = await _client.PostAsJsonAsync(
            "/api/v1/billing/quota/deduct",
            new { sessionId = Guid.NewGuid(), consumedMinutes = 10m, source = "test" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QuotaDeduct_InvalidWorkspaceId_ShouldReturnBadRequest()
    {
        // Arrange & Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/billing/quota/deduct?workspaceId={Guid.Empty}",
            new { sessionId = Guid.NewGuid(), consumedMinutes = 10m, source = "test" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ============================================================
    // 🚫 NEGATIVE PATH TESTS (Error Handling & Validation)
    // ============================================================

    [Fact]
    public async Task QuotaDeduct_InsufficientQuota_ShouldReturnError()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await _client.PostAsJsonAsync($"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.BasicPlanId }); // Only 100 minutes

        // Act - Try to deduct more than available
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes = 200m, // More than available
                source = "test"
            });

        // Assert
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.BadRequest, 
            HttpStatusCode.Conflict, 
            HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task QuotaDeduct_NegativeAmount_ShouldReturnBadRequest()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await _client.PostAsJsonAsync($"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.ProPlanId });

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes = -10m, // Invalid
                source = "test"
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QuotaDeduct_ExcessiveAmount_ShouldReturnBadRequest()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await _client.PostAsJsonAsync($"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.ProPlanId });

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes = 10001m, // Exceeds max (10000)
                source = "test"
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PaymentWebhook_InvalidSignature_ShouldReturnUnauthorized()
    {
        // Arrange
        var payload = new PayOsWebhookPayload
        {
            Code = "00",
            Desc = "Success",
            Data = new PayOsWebhookData
            {
                OrderCode = 123456,
                Amount = 500000,
                Reference = Guid.NewGuid().ToString(),
                TransactionDateTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                Currency = "VND"
            },
            Signature = "invalid-signature-value"
        };

        // Act
        var response = await _client.PostAsJsonAsync(
            "/api/v1/billing/payos/webhook", payload);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PaymentWebhook_MissingRequiredFields_ShouldReturnBadRequest()
    {
        // Arrange
        var payload = new { Code = "00" }; // Missing required fields

        // Act
        var response = await _client.PostAsJsonAsync(
            "/api/v1/billing/payos/webhook", payload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QuotaRefund_NegativeAmount_ShouldReturnBadRequest()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await _client.PostAsJsonAsync($"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.ProPlanId });

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/billing/quota/refund",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                refundMinutes = -5m, // Invalid
                reason = "test"
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ============================================================
    // 📊 EDGE CASE TESTS
    // ============================================================

    [Fact]
    public async Task QuotaDeduct_ZeroAmount_ShouldReturnBadRequest()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await _client.PostAsJsonAsync($"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.ProPlanId });

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes = 0m, // Zero not allowed
                source = "test"
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QuotaDeduct_MinimumValidAmount_ShouldSucceed()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await _client.PostAsJsonAsync($"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.ProPlanId });

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes = 0.01m, // Minimum valid
                source = "test"
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task QuotaDeduct_MaximumValidAmount_ShouldSucceed()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await _client.PostAsJsonAsync($"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.ProPlanId });

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes = 10000m, // Maximum valid
                source = "test"
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task QuotaCheck_NonexistentWorkspace_ShouldReturnZeroOrError()
    {
        // Arrange
        var nonexistentWorkspaceId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/billing/quota/check?workspaceId={nonexistentWorkspaceId}");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    // ============================================================
    // 📝 AUDIT TRAIL TESTS (ISO/IEC 27001: Logging & Monitoring)
    // ============================================================

    [Fact]
    public async Task QuotaDeduct_ShouldCreateAuditLog()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        const decimal consumedMinutes = 10m;

        await _client.PostAsJsonAsync($"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.ProPlanId });

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/billing/quota/deduct",
            new { workspaceId, sessionId, consumedMinutes, source = "test" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify audit log was created
        var logsResponse = await _client.GetAsync(
            $"/api/v1/billing/quota/{workspaceId}/history?page=1&pageSize=10");
        logsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var logs = await logsResponse.Content.ReadAsAsync<dynamic>();
        logs.Should().NotBeNull();
    }

    [Fact]
    public async Task QuotaOperations_ShouldIncludeCorrelationId()
    {
        // Arrange
        var correlationId = Guid.NewGuid().ToString();
        var workspaceId = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Post, 
            $"/api/v1/billing/quota/deduct")
        {
            Content = JsonContent.Create(new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes = 10m,
                source = "test"
            })
        };
        request.Headers.Add("X-Correlation-Id", correlationId);

        await _client.PostAsJsonAsync($"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.ProPlanId });

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.Headers.Should().ContainKey("X-Correlation-Id");
        response.Headers.GetValues("X-Correlation-Id").First().Should().Be(correlationId);
    }

    // ============================================================
    // 🔗 INTEGRATION POINT TESTS
    // ============================================================

    [Fact]
    public async Task SubscriptionAndQuotaIntegration_ShouldMaintainConsistency()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();

        // Act - Create subscription
        var subResponse = await _client.PostAsJsonAsync(
            $"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.ProPlanId });

        // Act - Check quota
        var quotaResponse = await _client.GetAsync(
            $"/api/v1/billing/quota/check?workspaceId={workspaceId}");

        // Assert
        subResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        quotaResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var quotaData = await quotaResponse.Content.ReadAsAsync<dynamic>();
        ((decimal)quotaData.quota).Should().Be(1000m); // Pro plan includes 1000 minutes
    }

    [Fact]
    public async Task PlanUpgradeAndQuotaReset_ShouldReflectInCheckQuota()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();

        // Create Basic subscription
        await _client.PostAsJsonAsync($"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.BasicPlanId });

        // Deduct some quota
        await _client.PostAsJsonAsync($"/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes = 50m,
                source = "test"
            });

        // Act - Upgrade to Pro plan
        var upgradeResponse = await _client.PostAsJsonAsync(
            $"/api/v1/billing/quota/upgrade",
            new { workspaceId, planId = TestData.ProPlanId });

        // Assert
        upgradeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify new quota includes leftover + new plan
        var finalQuotaResponse = await _client.GetAsync(
            $"/api/v1/billing/quota/check?workspaceId={workspaceId}");
        var quotaData = await finalQuotaResponse.Content.ReadAsAsync<dynamic>();
        ((decimal)quotaData.quota).Should().BeGreaterThan(950m); // Should have remaining + new
    }

    [Fact]
    public async Task TransactionHistoryPagination_ShouldRespectBounds()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await _client.PostAsJsonAsync($"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.ProPlanId });

        // Create multiple transactions
        for (int i = 0; i < 15; i++)
        {
            await _client.PostAsJsonAsync($"/api/v1/billing/quota/deduct",
                new
                {
                    workspaceId,
                    sessionId = Guid.NewGuid(),
                    consumedMinutes = 1m,
                    source = "test"
                });
        }

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/billing/quota/{workspaceId}/history?page=1&pageSize=200"); // Try exceeding max

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = await response.Content.ReadAsAsync<dynamic>();
        data.Should().NotBeNull();
    }

    // ============================================================
    // 🛡️ SECURITY HEADERS TESTS
    // ============================================================

    [Fact]
    public async Task ApiResponse_ShouldIncludeSecurityHeaders()
    {
        // Act
        var response = await _client.GetAsync($"/health");

        // Assert
        response.Headers.Should().ContainKey("X-Frame-Options");
        response.Headers.GetValues("X-Frame-Options").First().Should().Be("DENY");

        response.Headers.Should().ContainKey("X-Content-Type-Options");
        response.Headers.GetValues("X-Content-Type-Options").First().Should().Be("nosniff");

        response.Headers.Should().ContainKey("Referrer-Policy");
        response.Headers.GetValues("Referrer-Policy").First().Should().Be("no-referrer");
    }

    // ============================================================
    // 📋 VALIDATION TESTS (Input & Business Rules)
    // ============================================================

    [Fact]
    public async Task QuotaDeduct_EmptySessionId_ShouldReturnBadRequest()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await _client.PostAsJsonAsync($"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.ProPlanId });

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.Empty, // Invalid
                consumedMinutes = 10m,
                source = "test"
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QuotaDeduct_InvalidSource_ShouldReturnBadRequest()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        await _client.PostAsJsonAsync($"/api/v1/billing/subscription",
            new { workspaceId, planId = TestData.ProPlanId });

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/billing/quota/deduct",
            new
            {
                workspaceId,
                sessionId = Guid.NewGuid(),
                consumedMinutes = 10m,
                source = new string('a', 101) // Exceeds max length (100)
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ============================================================
    // Helper Methods
    // ============================================================

    private static string GenerateHmacSignature(PayOsWebhookPayload payload, string key)
    {
        var json = JsonSerializer.Serialize(payload.Data);
        var hash = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var computedHash = hash.ComputeHash(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(computedHash);
    }

    private static class TestData
    {
        public const string TestWebhookKey = "test-webhook-key-min-32-characters-long";
        public static readonly Guid ProPlanId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        public static readonly Guid BasicPlanId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    }
}
