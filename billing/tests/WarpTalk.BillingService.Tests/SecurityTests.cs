using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.BillingService.Tests;

public class SecurityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SecurityTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:RequireAuthentication"] = "false",
                    ["Billing:AutoSeedOnStartup"] = "false"
                });
            });

            builder.ConfigureServices(services =>
            {
                var dbContextDescriptors = services.Where(d =>
                    d.ServiceType == typeof(DbContextOptions<BillingDbContext>) ||
                    d.ServiceType == typeof(BillingDbContext) ||
                    d.ServiceType.Name.Contains("DbContextOptions")).ToList();

                foreach (var descriptor in dbContextDescriptors)
                {
                    services.Remove(descriptor);
                }

                services.AddDbContext<BillingDbContext>(options =>
                {
                    options.UseInMemoryDatabase("SecurityTestDb");
                });
            });
        });
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

        // Seed data for workspaceA
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarpTalk.BillingService.Infrastructure.Persistence.BillingDbContext>();
            var plan = new WarpTalk.BillingService.Domain.Entities.SubscriptionPlan
            {
                Id = Guid.NewGuid(),
                Name = PlanType.Pro,
                BaseQuotaMinutes = 100,
                PriceVnd = 10,
                MaxParticipants = 10,
                CreatedAt = DateTime.UtcNow
            };
            db.SubscriptionPlans.Add(plan);

            db.UsageQuotas.Add(new WarpTalk.BillingService.Domain.Entities.UsageQuota
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceA,
                PlanId = plan.Id,
                TotalAllocatedMinutes = 100,
                ConsumedMinutes = 10
            });
            await db.SaveChangesAsync();
        }

        var requestBody = new QuotaDeductRequest(
            sessionId: Guid.NewGuid(),
            consumedMinutes: 10,
            source: "Test"
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
