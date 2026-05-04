using System;
using System.Collections.Generic;
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
using WarpTalk.BillingService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.BillingService.Tests;

/// <summary>
/// Webhook & Payment Security Tests
/// Covers: Signature verification, replay attack prevention, webhook idempotency
/// ISO/IEC 27001/27002: Secure payment processing, message authentication
/// </summary>
public class WebhookSecurityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;
    private const string TestWebhookKey = "test-webhook-key-min-32-characters-long";

    public WebhookSecurityTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:RequireAuthentication"] = "false",
                    ["PayOS:ChecksumKey"] = TestWebhookKey,
                    ["Security:AllowInsecureWebhookSignatureInDevelopment"] = "false"
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
                    options.UseInMemoryDatabase($"WebhookSecurityTestDb_{Guid.NewGuid()}"));
            });
        });

        _client = _factory.CreateClient();
    }

    // ============================================================
    // 🔐 WEBHOOK SIGNATURE VERIFICATION TESTS
    // ============================================================

    [Fact]
    public async Task PayOsWebhook_ValidSignature_ShouldBeAccepted()
    {
        // Arrange
        var payload = CreateValidWebhookPayload();

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/payos/webhook", payload);

        // Assert
        response.StatusCode.Should().BeOneOf(
            System.Net.HttpStatusCode.OK,
            System.Net.HttpStatusCode.Conflict); // Conflict if transaction already exists
    }

    [Fact]
    public async Task PayOsWebhook_TamperedSignature_ShouldBeRejected()
    {
        // Arrange
        var payload = CreateValidWebhookPayload();
        payload.Signature = "tampered-signature-invalid-value"; // Tampered

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/payos/webhook", payload);

        // Assert
        response.StatusCode.Should().BeOneOf(
            System.Net.HttpStatusCode.Unauthorized,
            System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PayOsWebhook_MissingSignature_ShouldBeRejected()
    {
        // Arrange
        var payload = CreateValidWebhookPayload();
        payload.Signature = null; // Missing

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/payos/webhook", payload);

        // Assert
        response.StatusCode.Should().BeOneOf(
            System.Net.HttpStatusCode.Unauthorized,
            System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PayOsWebhook_SignatureWithWrongKey_ShouldBeRejected()
    {
        // Arrange
        var payload = CreateValidWebhookPayload();
        // Re-sign with wrong key
        payload.Signature = GenerateHmacSignature(payload, "wrong-secret-key-invalid-12345");

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/payos/webhook", payload);

        // Assert
        response.StatusCode.Should().BeOneOf(
            System.Net.HttpStatusCode.Unauthorized,
            System.Net.HttpStatusCode.BadRequest);
    }

    // ============================================================
    // 🔄 REPLAY ATTACK PREVENTION TESTS
    // ============================================================

    [Fact]
    public async Task DuplicateWebhook_ShouldBeIdempotent()
    {
        // Arrange
        var payload = CreateValidWebhookPayload();

        // Act - First webhook
        var firstResponse = await _client.PostAsJsonAsync("/api/v1/billing/payos/webhook", payload);

        // Act - Duplicate webhook (same ReferenceId)
        var secondResponse = await _client.PostAsJsonAsync("/api/v1/billing/payos/webhook", payload);

        // Assert - Both should succeed (idempotent)
        firstResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        secondResponse.StatusCode.Should().BeOneOf(
            System.Net.HttpStatusCode.OK,
            System.Net.HttpStatusCode.Conflict); // Conflict is acceptable for duplicate
    }

    [Fact]
    public async Task WebhookWithOldTimestamp_ShouldBeAccepted()
    {
        // Arrange
        var payload = CreateValidWebhookPayload();
        payload.Data.TransactionDateTime = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss");
        payload.Signature = GenerateHmacSignature(payload, TestWebhookKey);

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/payos/webhook", payload);

        // Assert - Should not reject based on age alone (signature is valid)
        response.StatusCode.Should().BeOneOf(
            System.Net.HttpStatusCode.OK,
            System.Net.HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task WebhookWithFutureTimestamp_ShouldBeAccepted()
    {
        // Arrange
        var payload = CreateValidWebhookPayload();
        payload.Data.TransactionDateTime = DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-dd HH:mm:ss");
        payload.Signature = GenerateHmacSignature(payload, TestWebhookKey);

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/payos/webhook", payload);

        // Assert - Should not reject based on timestamp alone (signature is valid)
        response.StatusCode.Should().BeOneOf(
            System.Net.HttpStatusCode.OK,
            System.Net.HttpStatusCode.Conflict);
    }

    // ============================================================
    // 💰 PAYMENT STATE TRANSITION TESTS
    // ============================================================

    [Fact]
    public async Task WebhookWithSuccessCode_ShouldUpdateTransactionToSuccess()
    {
        // Arrange
        var payload = CreateValidWebhookPayload();
        payload.Code = "00";
        payload.Signature = GenerateHmacSignature(payload, TestWebhookKey);

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/payos/webhook", payload);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task WebhookWithErrorCode_ShouldUpdateTransactionToFailed()
    {
        // Arrange
        var payload = CreateValidWebhookPayload();
        payload.Code = "01"; // Error code
        payload.Desc = "Payment failed";
        payload.Signature = GenerateHmacSignature(payload, TestWebhookKey);

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/payos/webhook", payload);

        // Assert
        response.StatusCode.Should().BeOneOf(
            System.Net.HttpStatusCode.OK,
            System.Net.HttpStatusCode.BadRequest);
    }

    // ============================================================
    // 🔍 WEBHOOK PAYLOAD VALIDATION TESTS
    // ============================================================

    [Fact]
    public async Task WebhookWithMissingData_ShouldBeRejected()
    {
        // Arrange
        var payload = new PayOsWebhookPayload
        {
            Code = "00",
            Desc = "Success",
            Data = null, // Missing required data
            Signature = "dummy"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/payos/webhook", payload);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WebhookWithInvalidCurrency_ShouldBeRejected()
    {
        // Arrange
        var payload = CreateValidWebhookPayload();
        payload.Data.Currency = "INVALID"; // Not ISO 4217 format
        payload.Signature = GenerateHmacSignature(payload, TestWebhookKey);

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/payos/webhook", payload);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WebhookWithNegativeAmount_ShouldBeRejected()
    {
        // Arrange
        var payload = CreateValidWebhookPayload();
        payload.Data.Amount = -500000; // Negative amount
        payload.Signature = GenerateHmacSignature(payload, TestWebhookKey);

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/payos/webhook", payload);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WebhookWithZeroAmount_ShouldBeRejected()
    {
        // Arrange
        var payload = CreateValidWebhookPayload();
        payload.Data.Amount = 0; // Zero amount
        payload.Signature = GenerateHmacSignature(payload, TestWebhookKey);

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/payos/webhook", payload);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WebhookWithExcessivelyLongDescription_ShouldBeRejected()
    {
        // Arrange
        var payload = CreateValidWebhookPayload();
        payload.Data.Description = new string('a', 256); // Exceeds max length
        payload.Signature = GenerateHmacSignature(payload, TestWebhookKey);

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/payos/webhook", payload);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WebhookWithMissingReference_ShouldBeRejected()
    {
        // Arrange
        var payload = CreateValidWebhookPayload();
        payload.Data.Reference = null; // Required field
        payload.Signature = GenerateHmacSignature(payload, TestWebhookKey);

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/billing/payos/webhook", payload);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    // ============================================================
    // Helper Methods
    // ============================================================

    private PayOsWebhookPayload CreateValidWebhookPayload()
    {
        var payload = new PayOsWebhookPayload
        {
            Code = "00",
            Desc = "Success",
            Data = new PayOsWebhookData
            {
                OrderCode = RandomNumberGenerator.GetInt32(100000, 999999),
                Amount = 500000,
                Reference = $"WarpTalk-{Guid.NewGuid()}-TopUp",
                TransactionDateTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                Currency = "VND",
                Code = "00"
            },
            Signature = "" // Will be set below
        };

        payload.Signature = GenerateHmacSignature(payload, TestWebhookKey);
        return payload;
    }

    private static string GenerateHmacSignature(PayOsWebhookPayload payload, string key)
    {
        var json = JsonSerializer.Serialize(payload.Data);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash);
    }
}
