using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using WarpTalk.NotificationService.Application.DTOs.AdminNotifications;
using WarpTalk.NotificationService.Domain.Constants;
using Xunit;

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.NotificationService.Tests.API.Controllers;

public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[] { 
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "admin")
        };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, "Test");

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public class AdminNotificationControllerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public AdminNotificationControllerTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                "Grpc:InternalSecret",
                "test-only-internal-grpc-secret-32-characters");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IConnectionMultiplexer>();
                services.RemoveAll<IAdminNotificationService>();

                var mockRedis = new Mock<IConnectionMultiplexer>();
                services.AddSingleton(mockRedis.Object);

                var mockAdminNotificationService = new Mock<IAdminNotificationService>();
                mockAdminNotificationService
                    .Setup(s => s.GetAdminNotificationsAsync(It.IsAny<GetAdminNotificationsQuery>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Result.Success(new AdminNotificationPaginatedResponse([], 0, 1, 10)));
                mockAdminNotificationService
                    .Setup(s => s.GetAdminNotificationDetailAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Result.Failure<AdminNotificationDetailDto>("Not found", ErrorCodes.NotFound));

                services.AddSingleton(mockAdminNotificationService.Object);

                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", options => { });
                
                services.AddAuthorization(options =>
                {
                    options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder("Test")
                        .RequireAuthenticatedUser()
                        .Build();
                });
            });
        }).CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Test");
    }

    [Fact]
    public async Task CreateAdminNotification_ShouldReject_UnknownTopLevelFields()
    {
        // Arrange
        // We construct an anonymous object with an extra field that shouldn't exist
        var payloadWithUnknownField = new
        {
            title = "Test Title",
            content = "Test Content",
            type = NotificationConstants.TypeSystem,
            targetAudienceMode = NotificationConstants.TargetModeBroadcast,
            unknown_extra_field = "This should be rejected"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/admin/notifications", payloadWithUnknownField);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("unknown_extra_field", content); // Expected binding error mention
    }

    [Fact]
    public async Task GetAdminNotifications_ReturnsOk()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/notifications?page=1&pageSize=10");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetAdminNotificationDetail_WhenNotFound_ReturnsNotFound()
    {
        // Act
        var response = await _client.GetAsync($"/api/v1/admin/notifications/{Guid.NewGuid()}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
