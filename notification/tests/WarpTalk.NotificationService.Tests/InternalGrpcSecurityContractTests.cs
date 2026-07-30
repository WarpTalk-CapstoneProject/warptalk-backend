namespace WarpTalk.NotificationService.Tests;

public sealed class InternalGrpcSecurityContractTests
{
    [Fact]
    public void EveryGrpcServer_UsesTheSharedInternalAuthenticationInterceptor()
    {
        var root = FindBackendRoot();
        var serverPrograms = new[]
        {
            "auth/src/WarpTalk.AuthService.API/Program.cs",
            "billing/src/WarpTalk.BillingService.API/Program.cs",
            "notification/src/WarpTalk.NotificationService.API/Program.cs",
            "transcript/src/WarpTalk.TranscriptService.API/Program.cs",
            "translation-room/src/WarpTalk.TranslationRoomService.API/Program.cs",
            "workspace/src/WarpTalk.WorkspaceService.API/Program.cs"
        };

        foreach (var relativePath in serverPrograms)
        {
            var source = File.ReadAllText(Path.Combine(root, relativePath));
            Assert.Contains(
                "AddWarpTalkGrpcServer",
                source,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryGrpcClientRegistration_UsesSharedAuthenticationAndResilience()
    {
        var root = FindBackendRoot();
        var clientRegistrationFiles = new[]
        {
            "auth/src/WarpTalk.AuthService.API/Program.cs",
            "billing/src/WarpTalk.BillingService.API/Program.cs",
            "gateway/src/WarpTalk.Gateway/Program.cs",
            "meeting/src/WarpTalk.MeetingService.API/Program.cs",
            "transcript/src/WarpTalk.TranscriptService.API/Program.cs",
            "translation-room/src/WarpTalk.TranslationRoomService.API/Program.cs",
            "workspace/src/WarpTalk.WorkspaceService.Infrastructure/DependencyInjection.cs"
        };

        foreach (var relativePath in clientRegistrationFiles)
        {
            var source = File.ReadAllText(Path.Combine(root, relativePath));
            var registrations = CountOccurrences(source, "AddGrpcClient<");
            var hardenedRegistrations = CountOccurrences(
                source,
                "AddWarpTalkGrpcClientDefaults");

            Assert.True(
                registrations > 0,
                $"{relativePath} must contain at least one gRPC client registration.");
            Assert.Equal(registrations, hardenedRegistrations);
        }
    }

    [Fact]
    public void SharedGrpcDefaults_DoNotBlindlyRetryEveryRpc()
    {
        var root = FindBackendRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "shared/WarpTalk.Shared/Grpc/InternalGrpcSecurity.cs"));

        Assert.DoesNotContain(
            "Names = { MethodName.Default }",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StreamingGrpcCalls_DoNotReceiveTheUnaryDeadline()
    {
        var root = FindBackendRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "shared/WarpTalk.Shared/Grpc/InternalGrpcSecurity.cs"));

        Assert.Contains("WithSecurityHeaders(context)", source, StringComparison.Ordinal);
        Assert.Contains("FailureWindow", source, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FindBackendRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "warptalk-backend.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the backend repository root.");
    }
}
