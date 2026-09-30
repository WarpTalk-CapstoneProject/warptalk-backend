using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Clients;
using WarpTalk.Shared.Protos;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// AllowAnyPlugins, read two ways from one call: fail-closed for the guard, tri-state for the
/// marketplace transition that writes the answer down.
/// </summary>
public class WorkspacePluginPolicyGrpcClientTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly WorkspaceService.WorkspaceServiceClient _grpc = Substitute.For<WorkspaceService.WorkspaceServiceClient>();

    private WorkspacePluginPolicyGrpcClient Sut() => new(_grpc, NullLogger<WorkspacePluginPolicyGrpcClient>.Instance);

    [Theory]
    [InlineData(true, WorkspacePluginPolicyAnswer.Allowed)]
    [InlineData(false, WorkspacePluginPolicyAnswer.NotAllowed)]
    public async Task AnAnswer_IsReportedAsIs(bool allowAnyPlugins, WorkspacePluginPolicyAnswer expected)
    {
        Answers(Task.FromResult(new GetWorkspaceSettingsResponse { AllowAnyPlugins = allowAnyPlugins }));

        Assert.Equal(expected, await Sut().ReadAllowAnyPluginsAsync(WorkspaceId));
        Assert.Equal(allowAnyPlugins, await Sut().AllowsPluginUsageAsync(WorkspaceId));
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.NotFound)]
    public async Task NoAnswer_IsUnknown_ToTheTransition_AndNo_ToTheGuard(StatusCode status)
    {
        Answers(Task.FromException<GetWorkspaceSettingsResponse>(new RpcException(new Status(status, "test"))));

        Assert.Equal(WorkspacePluginPolicyAnswer.Unknown, await Sut().ReadAllowAnyPluginsAsync(WorkspaceId));
        Assert.False(await Sut().AllowsPluginUsageAsync(WorkspaceId));
    }

    private void Answers(Task<GetWorkspaceSettingsResponse> response) =>
        _grpc.GetWorkspaceSettingsAsync(
                Arg.Any<GetWorkspaceSettingsRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => new AsyncUnaryCall<GetWorkspaceSettingsResponse>(
                response,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));
}
