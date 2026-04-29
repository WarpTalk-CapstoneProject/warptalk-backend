using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using WarpTalk.BillingService.Application.DTOs;
using Xunit;

namespace WarpTalk.BillingService.Tests;

public class SecurityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SecurityTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Request_MissingWorkspaceHeader_ShouldReturnBadRequest()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/v1/billing/quota/check");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Response_ShouldContainSecurityHeaders()
    {
        // Arrange
        var client = _factory.CreateClient();
        var workspaceId = Guid.NewGuid();

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/billing/quota/check");
        request.Headers.Add("X-Workspace-Id", workspaceId.ToString());
        var response = await client.SendAsync(request);

        // Assert
        response.Headers.Contains("X-Frame-Options").Should().BeTrue();
        response.Headers.GetValues("X-Frame-Options").First().Should().Be("DENY");
        
        response.Headers.Contains("X-Content-Type-Options").Should().BeTrue();
        response.Headers.GetValues("X-Content-Type-Options").First().Should().Be("nosniff");
    }

    [Fact]
    public async Task DeductQuota_WithWrongWorkspaceId_ShouldReturnNotFound()
    {
        // Arrange
        var client = _factory.CreateClient();
        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid(); // Workspace này không có dữ liệu

        var requestBody = new QuotaDeductRequest(
            SessionId: Guid.NewGuid(),
            ConsumedMinutes: 10,
            Source: "Test"
        );

        // Act
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/quota/deduct")
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("X-Workspace-Id", workspaceB.ToString());
        request.Headers.Add("Idempotency-Key", "test-key");

        var response = await client.SendAsync(request);

        // Assert
        // Vì workspaceB không có Quota được seed, hệ thống phải trả về lỗi nghiệp vụ (Success = false)
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var result = await response.Content.ReadFromJsonAsync<QuotaDeductResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("QuotaNotFound");
    }
}
