using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Extensions;
using WarpTalk.MeetingService.Infrastructure.Storage;

namespace WarpTalk.MeetingService.Tests.Services;

public sealed class MeetingChatFileStorageRegistrationTests
{
    [Fact]
    public void AddMeetingChatFileStorage_DevelopmentLocal_RegistersLocalAdapter()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Local"
        });

        services.AddMeetingChatFileStorage(
            configuration,
            MockEnvironment(Environments.Development));

        using var provider = services.BuildServiceProvider();
        Assert.IsType<LocalMeetingChatFileStorage>(
            provider.GetRequiredService<IMeetingChatFileStorage>());
    }

    [Fact]
    public void AddMeetingChatFileStorage_ProductionLocal_FailsFast()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Local"
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddMeetingChatFileStorage(
                configuration,
                MockEnvironment(Environments.Production)));

        Assert.Contains("S3-compatible", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddMeetingChatFileStorage_ProductionPlaceholderCredentials_FailsFast()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "MinIO",
            ["Storage:S3:ServiceUrl"] = "http://minio:9000",
            ["Storage:S3:AccessKey"] = "CHANGE_ME",
            ["Storage:S3:SecretKey"] = "placeholder",
            ["Storage:S3:BucketName"] = "warptalk-meeting-chat"
        });

        Assert.Throws<InvalidOperationException>(() =>
            services.AddMeetingChatFileStorage(
                configuration,
                MockEnvironment(Environments.Production)));
    }

    [Fact]
    public void AddMeetingChatFileStorage_ProductionS3_RegistersSharedAdapter()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "MinIO",
            ["Storage:S3:ServiceUrl"] = "http://minio:9000",
            ["Storage:S3:AccessKey"] = "warptalk-meeting",
            ["Storage:S3:SecretKey"] = "a-production-secret-with-enough-entropy",
            ["Storage:S3:BucketName"] = "warptalk-meeting-chat"
        });

        services.AddMeetingChatFileStorage(
            configuration,
            MockEnvironment(Environments.Production));

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IAmazonS3));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IMeetingChatFileStorage)
            && descriptor.ImplementationType == typeof(S3MeetingChatFileStorage));
    }

    private static IConfiguration BuildConfiguration(
        IDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private static IHostEnvironment MockEnvironment(string name)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(item => item.EnvironmentName).Returns(name);
        return environment.Object;
    }
}
