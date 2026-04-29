using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Infrastructure.Persistence;
using Xunit;
using FluentAssertions;

namespace WarpTalk.BillingService.Tests;

public class IntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public IntegrationTests(WebApplicationFactory<Program> factory)
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
                // Deep remove any DB-related services
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
                    options.UseInMemoryDatabase("IntegrationTestDb");
                });

            });


        });
    }

    [Fact]
    public async Task GetQuotaCheck_WithoutHeader_ReturnsBadRequest()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/v1/billing/quota/check");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetQuotaCheck_WithValidHeader_ReturnsOk()
    {
        // Arrange
        var client = _factory.CreateClient();
        var workspaceId = Guid.NewGuid();
        
        // Seed some data into InMemory DB
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            
            var plan = new Domain.Entities.SubscriptionPlan 
            { 
                Id = Guid.NewGuid(), 
                Name = Domain.Enums.PlanType.Pro, 
                BaseQuotaMinutes = 500,
                CreatedAt = DateTime.UtcNow
            };
            db.SubscriptionPlans.Add(plan);

            db.UsageQuotas.Add(new Domain.Entities.UsageQuota 
            { 
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                PlanId = plan.Id,
                TotalAllocatedMinutes = 100,
                ConsumedMinutes = 10
            });
            await db.SaveChangesAsync();
        }

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/billing/quota/check");
        request.Headers.Add("X-Workspace-Id", workspaceId.ToString());
        
        var response = await client.SendAsync(request);

        // Assert
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new Exception($"Test failed with {response.StatusCode}. Body: {body}");
        }

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadFromJsonAsync<QuotaCheckResponse>();
        content.Should().NotBeNull();
        content.HasQuota.Should().BeTrue();
    }
}
