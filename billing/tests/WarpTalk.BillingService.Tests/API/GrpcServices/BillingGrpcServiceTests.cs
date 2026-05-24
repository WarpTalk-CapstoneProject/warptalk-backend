using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using WarpTalk.BillingService.API.GrpcServices;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared.Protos;
using Xunit;

namespace WarpTalk.BillingService.Tests.API.GrpcServices;

public class BillingGrpcServiceTests
{
    private readonly Mock<ICreditService> _mockCreditService;
    private readonly Mock<ISubscriptionService> _mockSubscriptionService;
    private readonly Mock<IPlanService> _mockPlanService;
    private readonly Mock<IPaymentService> _mockPaymentService;
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<IConnectionMultiplexer> _mockRedis;
    private readonly Mock<IDatabase> _mockRedisDb;
    private readonly Mock<ILogger<BillingGrpcService>> _mockLogger;
    private readonly BillingGrpcService _service;

    public BillingGrpcServiceTests()
    {
        _mockCreditService = new Mock<ICreditService>();
        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockPlanService = new Mock<IPlanService>();
        _mockPaymentService = new Mock<IPaymentService>();
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockRedis = new Mock<IConnectionMultiplexer>();
        _mockRedisDb = new Mock<IDatabase>();
        _mockLogger = new Mock<ILogger<BillingGrpcService>>();

        _mockRedis.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_mockRedisDb.Object);

        _service = new BillingGrpcService(
            _mockCreditService.Object,
            _mockSubscriptionService.Object,
            _mockPlanService.Object,
            _mockPaymentService.Object,
            _mockUnitOfWork.Object,
            _mockRedis.Object,
            _mockLogger.Object
        );
    }

    [Fact]
    public async Task RecordUsage_ShouldReject_WhenDurationExceedsMaxLimit()
    {
        // Arrange
        var request = new RecordUsageGrpcRequest
        {
            UserId = Guid.NewGuid().ToString(),
            HostWorkspaceId = Guid.NewGuid().ToString(),
            DurationSeconds = 5 * 3600, // 5 hours (over 4 hour limit)
            CreditsConsumed = 10
        };

        var contextMock = new Mock<ServerCallContext>();

        // Act
        var result = await _service.RecordUsage(request, contextMock.Object);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Duration exceeds maximum allowed limit");
        _mockCreditService.Verify(x => x.RecordUsageAsync(It.IsAny<WarpTalk.BillingService.Application.DTOs.RecordUsageRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordUsage_ShouldReject_WhenCreditsAreNegative()
    {
        // Arrange
        var request = new RecordUsageGrpcRequest
        {
            UserId = Guid.NewGuid().ToString(),
            HostWorkspaceId = Guid.NewGuid().ToString(),
            DurationSeconds = 60,
            CreditsConsumed = -5
        };

        var contextMock = new Mock<ServerCallContext>();

        // Act
        var result = await _service.RecordUsage(request, contextMock.Object);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Credits consumed cannot be negative");
        _mockCreditService.Verify(x => x.RecordUsageAsync(It.IsAny<WarpTalk.BillingService.Application.DTOs.RecordUsageRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }


}
