using System.Diagnostics;

namespace WarpTalk.TranscriptService.Tests.Helpers;

/// <summary>Same gate the gateway and billing integration tests use: skip, don't fail, without Docker.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!IsDockerAvailable())
        {
            Skip = "Docker is required for Redis Testcontainers integration tests.";
        }
    }

    private static bool IsDockerAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            return process is not null && process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
