using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Infrastructure.Adapters;

namespace WarpTalk.WorkspaceService.Tests;

public class DocumentSecurityScannerTests
{
    [Fact]
    public async Task ScanAsync_ShouldThrow_WhenWorkerUnavailable()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        database
            .StreamAddAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<NameValueEntry[]>(),
                Arg.Any<RedisValue>(),
                Arg.Any<int?>(),
                Arg.Any<bool>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromException<RedisValue>(new TimeoutException("worker unavailable")));

        var scanner = new DocumentSecurityScanner(
            redis,
            Substitute.For<ILogger<DocumentSecurityScanner>>());

        await Assert.ThrowsAsync<TimeoutException>(() =>
            scanner.ScanAsync("hello", piiEnabled: true, dlpEnabled: false, keywordsBlacklist: null, CancellationToken.None));
    }

    [Fact]
    public async Task ScanAsync_ShouldThrow_WhenWorkerReportsScanFailure()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        database
            .StreamAddAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<NameValueEntry[]>(),
                Arg.Any<RedisValue>(),
                Arg.Any<int?>(),
                Arg.Any<bool>(),
                Arg.Any<CommandFlags>())
            .Returns(new RedisValue("request-1"));
        database
            .StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(new RedisValue(
                """{"pii_detected":false,"dlp_detected":false,"violation_found":true,"masked_content":"","scan_failed":true}"""));

        var scanner = new DocumentSecurityScanner(
            redis,
            Substitute.For<ILogger<DocumentSecurityScanner>>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scanner.ScanAsync(
                "sensitive",
                piiEnabled: true,
                dlpEnabled: false,
                keywordsBlacklist: null,
                CancellationToken.None));
    }
}
