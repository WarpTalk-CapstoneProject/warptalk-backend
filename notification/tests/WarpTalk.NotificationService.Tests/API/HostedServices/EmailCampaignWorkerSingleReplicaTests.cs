using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.NotificationService.API.HostedServices;
using WarpTalk.NotificationService.Application.Services.EmailCms;
using WarpTalk.Shared.Coordination;
using Xunit;

namespace WarpTalk.NotificationService.Tests.API.HostedServices;

/// <summary>
/// k8s multi-replica dedupe. A send picks its next pending recipients and marks them sent only
/// after the provider call, so two replicas in the same tick email the same people twice. Each
/// tick runs on one replica, under the shared lease that is renewed for as long as the tick runs
/// (the fixed 30-second lock it replaced expired under a slow batch).
/// </summary>
public class EmailCampaignWorkerSingleReplicaTests
{
    private readonly Mock<IEmailCampaignService> _sends = new();
    private readonly IDistributedLockProvider _locks =
        new DistributedLockProvider(new InProcessLeaseStore(TimeProvider.System), TimeProvider.System);

    private EmailCampaignWorker NewReplica()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _sends.Object);
        return new EmailCampaignWorker(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _locks,
            new EmailCampaignOptions(),
            NullLogger<EmailCampaignWorker>.Instance);
    }

    [Fact]
    public async Task WhileOneReplicaIsSending_TheOtherReplicasTickSendsNothing()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sends.Setup(s => s.ProcessNextAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                started.TrySetResult();
                await release.Task;
                return true;
            });

        var first = NewReplica().RunTickAsync(CancellationToken.None);
        await started.Task;
        var second = await NewReplica().RunTickAsync(CancellationToken.None);
        release.SetResult();

        Assert.Equal(ExclusiveTickOutcome.Ran, await first);
        Assert.Equal(ExclusiveTickOutcome.Skipped, second);
        _sends.Verify(s => s.ProcessNextAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsecutiveTicks_EachRun_BecauseTheLeaseIsReleasedAfterATick()
    {
        _sends.Setup(s => s.ProcessNextAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        Assert.Equal(ExclusiveTickOutcome.Ran, await NewReplica().RunTickAsync(CancellationToken.None));
        Assert.Equal(ExclusiveTickOutcome.Ran, await NewReplica().RunTickAsync(CancellationToken.None));

        _sends.Verify(s => s.ProcessNextAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
