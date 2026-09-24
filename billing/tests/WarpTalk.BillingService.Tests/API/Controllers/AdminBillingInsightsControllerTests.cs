using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using WarpTalk.BillingService.API.Controllers;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Tests.API.Controllers;

/// <summary>
/// Runs the real routing + authentication + the shared system-admin policy + model binding over
/// <see cref="AdminBillingInsightsController"/> in an in-process TestServer, so 401/403/400 are the
/// statuses a caller would actually receive rather than attributes read by reflection.
/// </summary>
public sealed class AdminBillingInsightsControllerTests : IAsyncLifetime
{
    private const string Insights = "/api/v1/admin/billing/insights";
    private const string Snapshot = "/api/v1/admin/billing/insights/snapshot";
    private const string Pnl = "/api/v1/admin/billing/insights/pnl";

    private readonly Mock<IAdminBillingInsightsService> _service = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private bool _useRealService;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(AdminBillingInsightsController).Assembly);
        builder.Services
            .AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddWarpTalkSystemAdminAuthorization();
        builder.Services.AddScoped<IAdminBillingInsightsService>(_ => _useRealService
            // Validation runs before any data access, so the real service needs no database here.
            ? new AdminBillingInsightsService(
                Mock.Of<IUnitOfWork>(MockBehavior.Strict),
                Mock.Of<IUsageRateCardRepository>(MockBehavior.Strict),
                Mock.Of<IWorkspaceClient>(MockBehavior.Strict),
                NullLogger<AdminBillingInsightsService>.Instance)
            : _service.Object);

        _app = builder.Build();
        _app.UseRouting();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapControllers();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Theory]
    [InlineData(Insights + "?from=2026-09-01T00:00:00Z&to=2026-09-17T00:00:00Z")]
    [InlineData(Snapshot)]
    [InlineData(Pnl + "?from=2026-09-01T00:00:00Z&to=2026-09-17T00:00:00Z")]
    public async Task Anonymous_Is401(string url)
    {
        (await _client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(Insights + "?from=2026-09-01T00:00:00Z&to=2026-09-17T00:00:00Z", "Member")]
    [InlineData(Insights + "?from=2026-09-01T00:00:00Z&to=2026-09-17T00:00:00Z", "Admin")]
    [InlineData(Snapshot, "Owner")]
    [InlineData(Snapshot, "Admin")]
    [InlineData(Pnl + "?from=2026-09-01T00:00:00Z&to=2026-09-17T00:00:00Z", "Admin")]
    public async Task NonSystemAdmin_Is403(string url, string role)
    {
        // "Admin" (capital A) is the WORKSPACE administrator role; only lowercase "admin" is the platform one.
        (await Send(url, role)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("?from=2026-09-17T00:00:00Z&to=2026-09-01T00:00:00Z")]
    [InlineData("?from=2025-01-01T00:00:00Z&to=2026-09-01T00:00:00Z")]
    [InlineData("?from=2026-09-01T00:00:00Z&to=2026-09-17T00:00:00Z&compare=lastYear")]
    [InlineData("?from=2026-09-01T00:00:00Z&to=2026-09-17T00:00:00Z&tz=Asia/Atlantis")]
    public async Task InvalidRange_Is400(string queryString)
    {
        _useRealService = true;

        var response = await Send(Insights + queryString, SystemAdminAuthorization.RoleName);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ValidationError);
    }

    [Theory]
    [InlineData("?from=2026-09-17T00:00:00Z&to=2026-09-01T00:00:00Z")]
    [InlineData("?from=2026-09-01T00:00:00Z&to=2026-09-17T00:00:00Z&tz=Asia/Atlantis")]
    public async Task Pnl_InvalidRange_Is400(string queryString)
    {
        _useRealService = true;

        var response = await Send(Pnl + queryString, SystemAdminAuthorization.RoleName);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ValidationError);
    }

    [Theory]
    [InlineData("?tz=Asia/Atlantis")]
    [InlineData("?tz=..%2F..%2Fetc%2Fpasswd")]
    public async Task Snapshot_UnknownTz_Is400(string queryString)
    {
        _useRealService = true;

        var response = await Send(Snapshot + queryString, SystemAdminAuthorization.RoleName);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("tz");
    }

    [Fact]
    public async Task Snapshot_PassesTzThrough_AndSerializesNullsAsNull()
    {
        _service
            .Setup(s => s.GetSnapshotAsync("Asia/Ho_Chi_Minh", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new AdminBillingSnapshotDto(
                DateTime.UtcNow, null, "excludes 1 EUR rows", 4_480_000m, null, null, "excludes 2 EUR rows",
                0, new AdminActiveByCycleDto(0, 0, 0), new AdminChurnRateMonthDto(0, 0, null), 0, 0, 0, 0, 0, 0,
                new AdminOutstandingInvoicesDto(1, null, "excludes 1 EUR rows", 0, null, null), 0,
                [], [], [new AdminEndingSoonDto(Guid.Empty, null, "Team", DateTime.UtcNow, true)], [])));

        var response = await Send(Snapshot + "?tz=Asia/Ho_Chi_Minh", "admin");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("revenueToday").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("revenueTodayNote").GetString().Should().Be("excludes 1 EUR rows");
        root.GetProperty("revenueYesterdayNote").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("mrr").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("outstandingInvoices").GetProperty("amount").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("outstandingInvoices").GetProperty("amountNote").GetString().Should().Be("excludes 1 EUR rows");
        root.GetProperty("churnRateMonth").GetProperty("rate").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("endingSoon")[0].GetProperty("workspaceName").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task SystemAdmin_GetsTheContractShape()
    {
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);
        _service
            .Setup(s => s.GetInsightsAsync(It.IsAny<AdminInsightsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new AdminBillingInsightsDto(
                new AdminInsightRange(from, to),
                new AdminInsightRange(from.AddDays(-2), from),
                to,
                [new AdminInsightMetric("revenue", 48_900_000m, null, AdminInsightUnits.Money, true, "why")],
                [new AdminRevenueByDayDto("2026-09-01", 1_200_000m), new AdminRevenueByDayDto("2026-09-02", null)],
                "excludes 1 EUR rows",
                [new AdminRevenueByMonthDto("2026-09", 24_000_000m)],
                null,
                [new AdminCreditsByServiceDto("TRANSLATION", 4_378_400)],
                [new AdminTopWorkspaceCreditsDto(Guid.Empty, "Demo", 2_410_000)])));

        var response = await Send(Insights + "?from=2026-09-01T00:00:00Z&to=2026-09-03T00:00:00Z&compare=previous", "admin");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("range").GetProperty("from").GetDateTime().Should().Be(from);
        root.GetProperty("range").TryGetProperty("isEmpty", out _).Should().BeFalse();
        root.GetProperty("previousRange").GetProperty("to").GetDateTime().Should().Be(from);
        root.TryGetProperty("generatedAt", out _).Should().BeTrue();
        var metric = root.GetProperty("metrics")[0];
        metric.GetProperty("id").GetString().Should().Be("revenue");
        metric.GetProperty("value").GetDecimal().Should().Be(48_900_000m);
        metric.GetProperty("previous").ValueKind.Should().Be(JsonValueKind.Null);
        metric.GetProperty("unit").GetString().Should().Be("money");
        metric.GetProperty("higherIsBetter").GetBoolean().Should().BeTrue();
        metric.GetProperty("note").GetString().Should().Be("why");
        root.GetProperty("revenueByDay")[0].GetProperty("date").GetString().Should().Be("2026-09-01");
        root.GetProperty("revenueByDay")[1].GetProperty("revenue").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("revenueByDayNote").GetString().Should().Be("excludes 1 EUR rows");
        root.GetProperty("revenueByMonth")[0].GetProperty("month").GetString().Should().Be("2026-09");
        root.GetProperty("revenueByMonthNote").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("creditsByService")[0].GetProperty("usageType").GetString().Should().Be("TRANSLATION");
        root.GetProperty("topWorkspaces")[0].GetProperty("workspaceName").GetString().Should().Be("Demo");
    }

    private Task<HttpResponseMessage> Send(string url, string role)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestAuthHandler.RoleHeader, role);
        return _client.SendAsync(request);
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Test";
        public const string RoleHeader = "X-Test-Role";

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role.ToString())],
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
