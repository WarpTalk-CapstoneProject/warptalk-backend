using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Infrastructure.Workers;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests;

public class MeetingLifecycleWorkerTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldRunAndStopGracefully_WhenCancellationRequested()
    {
        // Arrange
        var logger = new NullLogger<MeetingLifecycleWorker>();
        var worker = new MeetingLifecycleWorker(logger);
        
        using var cts = new CancellationTokenSource();

        // Act
        // Start the worker task
        var workerTask = worker.StartAsync(cts.Token);
        
        // Let it run for a brief moment
        await Task.Delay(100);
        
        // Request cancellation to simulate shutdown
        cts.Cancel();
        
        await worker.StopAsync(CancellationToken.None);

        // Assert
        Assert.True(workerTask.IsCompleted);
    }
}
