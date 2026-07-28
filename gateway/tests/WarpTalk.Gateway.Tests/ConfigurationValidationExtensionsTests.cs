using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.Gateway.Tests;

public sealed class ConfigurationValidationExtensionsTests
{
    [Fact]
    public void GetRequiredServiceUri_ProductionMissingConfigurationFailsFast()
    {
        var configuration = new ConfigurationBuilder().Build();
        var environment = new TestHostEnvironment(Environments.Production);

        Assert.Throws<InvalidOperationException>(() =>
            configuration.GetRequiredServiceUri(
                environment,
                "GrpcUrls:WorkspaceServiceUrl",
                "http://localhost:50056"));
    }

    [Fact]
    public void RequirePublicBaseUrl_ProductionRejectsLocalhost()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppBaseUrl"] = "http://localhost:3000"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            configuration.RequirePublicBaseUrl(
                new TestHostEnvironment(Environments.Production),
                "AppBaseUrl"));
    }

    private sealed class TestHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "WarpTalk.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.PhysicalFileProvider(AppContext.BaseDirectory);
    }
}
