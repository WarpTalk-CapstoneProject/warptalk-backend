using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Extensions;
using WarpTalk.MeetingService.Infrastructure.Storage;

namespace WarpTalk.MeetingService.Tests.API;

// WT-330: the environment-aware guard in AddMeetingChatFileStorage was fully unit tested while
// Program.cs hard-wired LocalMeetingChatFileStorage, so the guard never ran in the real app and
// production wrote chat uploads to a read-only container path (500 on every upload).
//
// MeetingChatFileStorageRegistrationTests proves the helper behaves correctly. These tests pin the
// part that was actually broken: that Program.cs delegates to the helper instead of naming a
// concrete adapter, and that the composition Program.cs performs resolves as intended per
// environment.
public sealed class ProgramStorageWiringTests
{
    [Fact]
    public void Program_DelegatesChatFileStorageToEnvironmentAwareHelper()
    {
        var source = ReadProgramSource();

        Assert.Contains(
            "AddMeetingChatFileStorage(builder.Configuration, builder.Environment)",
            source,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(LocalMeetingChatFileStorage))]
    [InlineData(nameof(S3MeetingChatFileStorage))]
    public void Program_DoesNotHardWireAConcreteChatFileStorageAdapter(string adapterName)
    {
        var source = ReadProgramSource();

        // A registration naming a concrete adapter bypasses the guard; the helper must choose.
        var registration = $"IMeetingChatFileStorage, {adapterName}";
        var qualifiedRegistration =
            $"IMeetingChatFileStorage, WarpTalk.MeetingService.Infrastructure.Storage.{adapterName}";

        Assert.DoesNotContain(registration, source, StringComparison.Ordinal);
        Assert.DoesNotContain(qualifiedRegistration, source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramComposition_InDevelopmentWithoutStorageConfig_ResolvesLocalAdapter()
    {
        // Development with no Storage:* section is the default local-run shape.
        using var provider = BuildProviderAsProgramDoes(
            new Dictionary<string, string?>(),
            Environments.Development);

        Assert.IsType<LocalMeetingChatFileStorage>(
            provider.GetRequiredService<IMeetingChatFileStorage>());
    }

    [Fact]
    public void ProgramComposition_InProductionWithMinIoConfig_ResolvesS3Adapter()
    {
        // Mirrors deploy/production/app.compose.yml for meeting-service.
        using var provider = BuildProviderAsProgramDoes(
            new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "MinIO",
                ["Storage:S3:ServiceUrl"] = "http://10.0.0.10:9000",
                ["Storage:S3:AccessKey"] = "warptalk-admin",
                ["Storage:S3:SecretKey"] = "a-production-secret-with-enough-entropy",
                ["Storage:S3:BucketName"] = "warptalk-meeting-chat",
                ["Storage:S3:EnsureBucketExists"] = "false"
            },
            Environments.Production);

        Assert.IsType<S3MeetingChatFileStorage>(
            provider.GetRequiredService<IMeetingChatFileStorage>());
    }

    [Fact]
    public void ProgramComposition_InProductionWithoutS3Config_FailsFastAtStartup()
    {
        // The regression itself: production must refuse to boot rather than silently fall back to
        // local-disk writes that the container rejects at request time.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            BuildProviderAsProgramDoes(
                new Dictionary<string, string?>(),
                Environments.Production));

        Assert.Contains("S3-compatible", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ServiceProvider BuildProviderAsProgramDoes(
        IDictionary<string, string?> configurationValues,
        string environmentName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(item => item.EnvironmentName).Returns(environmentName);

        var services = new ServiceCollection();
        services.AddMeetingChatFileStorage(configuration, environment.Object);
        return services.BuildServiceProvider();
    }

    private static string ReadProgramSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "meeting",
                "src",
                "WarpTalk.MeetingService.API",
                "Program.cs");

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate meeting/src/WarpTalk.MeetingService.API/Program.cs from "
            + AppContext.BaseDirectory);
    }
}
