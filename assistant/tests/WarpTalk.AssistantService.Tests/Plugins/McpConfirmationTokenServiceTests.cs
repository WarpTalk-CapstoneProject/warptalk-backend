using System.Text.Json.Nodes;
using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Tests.Plugins;

public class McpConfirmationTokenServiceTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid PluginId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPluginConfirmationTokenRepository _repository = Substitute.For<IPluginConfirmationTokenRepository>();
    private readonly IMcpConfirmationTokenProtector _protector = Substitute.For<IMcpConfirmationTokenProtector>();

    public McpConfirmationTokenServiceTests()
    {
        _unitOfWork.PluginConfirmationTokenRepository.Returns(_repository);
        _protector.Protect(Arg.Any<McpConfirmationTokenPayloadDto>()).Returns("protected-token");
    }

    [Fact]
    public async Task CreateAsync_PersistsActionBoundTokenWithCanonicalArgumentHashAndFiveMinuteTtl()
    {
        McpConfirmationTokenPayloadDto? payload = null;
        _protector.Protect(Arg.Do<McpConfirmationTokenPayloadDto>(value => payload = value));
        var startedAt = DateTime.UtcNow;

        var result = await CreateSut().CreateAsync(UserId, PluginId, Request(new JsonObject
        {
            ["title"] = "Planning",
            ["start"] = "2026-08-26T09:00:00Z",
        }));

        Assert.True(result.IsSuccess);
        Assert.Equal("protected-token", result.Value);
        Assert.NotNull(payload);
        Assert.Equal(UserId, payload!.UserId);
        Assert.Equal(PluginId, payload.PluginId);
        Assert.Equal(PluginConstants.GoogleWorkspace, payload.PluginKey);
        Assert.Equal("google_calendar_create_event", payload.ToolName);
        Assert.InRange(payload.ExpiresAt, startedAt.AddMinutes(4), DateTime.UtcNow.AddMinutes(5));
        await _repository.Received(1).AddAsync(
            Arg.Is<PluginConfirmationToken>(token => token.ArgumentHash == payload.ArgumentHash),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_UsesSameHashWhenJsonPropertyOrderChanges()
    {
        McpConfirmationTokenPayloadDto? first = null;
        McpConfirmationTokenPayloadDto? second = null;
        _protector.Protect(Arg.Do<McpConfirmationTokenPayloadDto>(value =>
        {
            if (first == null) first = value;
            else second = value;
        }));

        await CreateSut().CreateAsync(UserId, PluginId, Request(new JsonObject
        {
            ["title"] = "Planning",
            ["start"] = "2026-08-26T09:00:00Z",
        }));
        await CreateSut().CreateAsync(UserId, PluginId, RequestWithSameAction(new JsonObject
        {
            ["start"] = "2026-08-26T09:00:00Z",
            ["title"] = "Planning",
        }));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.ArgumentHash, second!.ArgumentHash);
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_ConsumesMatchingToken()
    {
        var request = Request(new JsonObject { ["title"] = "Planning" });
        var payload = Payload(request, DateTime.UtcNow.AddMinutes(5));
        _protector.Unprotect("token").Returns(Result.Success(payload));
        _repository.TryConsumeAsync(payload.TokenId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await CreateSut().ValidateAndConsumeAsync(UserId, PluginId, request, "token");

        Assert.True(result.IsSuccess);
        await _repository.Received(1).TryConsumeAsync(payload.TokenId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_RejectsChangedArgumentsWithoutConsuming()
    {
        var request = Request(new JsonObject { ["title"] = "Planning" });
        var payload = Payload(request, DateTime.UtcNow.AddMinutes(5));
        _protector.Unprotect("token").Returns(Result.Success(payload));

        var result = await CreateSut().ValidateAndConsumeAsync(
            UserId,
            PluginId,
            Request(new JsonObject { ["title"] = "Changed" }),
            "token");

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.ErrorCode);
        await _repository.DidNotReceiveWithAnyArgs().TryConsumeAsync(default, default, default);
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_RejectsExpiredTokenAsConfirmationRequired()
    {
        var request = Request(new JsonObject { ["title"] = "Planning" });
        var payload = Payload(request, DateTime.UtcNow.AddMinutes(-1));
        _protector.Unprotect("token").Returns(Result.Success(payload));

        var result = await CreateSut().ValidateAndConsumeAsync(UserId, PluginId, request, "token");

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ConfirmationRequired, result.ErrorCode);
        await _repository.DidNotReceiveWithAnyArgs().TryConsumeAsync(default, default, default);
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_RejectsReplayWhenRepositoryCannotClaimToken()
    {
        var request = Request(new JsonObject { ["title"] = "Planning" });
        var payload = Payload(request, DateTime.UtcNow.AddMinutes(5));
        _protector.Unprotect("token").Returns(Result.Success(payload));
        _repository.TryConsumeAsync(payload.TokenId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await CreateSut().ValidateAndConsumeAsync(UserId, PluginId, request, "token");

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.ErrorCode);
    }

    private McpConfirmationTokenService CreateSut() =>
        new(_unitOfWork, _protector);

    private static McpToolExecutionRequest Request(JsonObject arguments) =>
        new(WorkspaceId, PluginConstants.GoogleWorkspace, "google_calendar_create_event", arguments, null, null, null);

    private static McpToolExecutionRequest RequestWithSameAction(JsonObject arguments) =>
        Request(arguments);

    private static McpConfirmationTokenPayloadDto Payload(McpToolExecutionRequest request, DateTime expiresAt)
    {
        return McpConfirmationTokenMapper.ToPayload(
            McpConfirmationTokenMapper.ToEntity(UserId, PluginId, request, DateTime.UtcNow, expiresAt));
    }
}
