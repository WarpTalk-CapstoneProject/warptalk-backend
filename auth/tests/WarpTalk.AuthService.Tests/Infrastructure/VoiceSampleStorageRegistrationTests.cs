using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Infrastructure.Extensions;
using WarpTalk.AuthService.Infrastructure.Storage;

namespace WarpTalk.AuthService.Tests.Infrastructure;

public sealed class VoiceSampleStorageRegistrationTests
{
    [Fact]
    public void DevelopmentLocalStorageIsAllowed()
    {
        var services = new ServiceCollection();
        services.AddVoiceSampleStorage(Configuration("Local"), Environment(Environments.Development));

        using var provider = services.BuildServiceProvider();
        Assert.IsType<LocalVoiceSampleStorage>(provider.GetRequiredService<IVoiceSampleStorage>());
    }

    [Fact]
    public void ProductionLocalStorageIsRejected()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddVoiceSampleStorage(Configuration("Local"), Environment(Environments.Production)));
    }

    private static IConfiguration Configuration(string provider) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = provider,
            ["Storage:S3:ServiceUrl"] = "http://minio:9000",
            ["Storage:S3:AccessKey"] = "auth-user",
            ["Storage:S3:SecretKey"] = "auth-secret",
            ["Storage:S3:BucketName"] = "warptalk-voice-samples"
        }).Build();

    private static IHostEnvironment Environment(string name)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(name);
        return environment;
    }
}
