using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WarpTalk.TranslationRoomService.Tests.Contracts;

/// <summary>
/// WT-431. Every configured gRPC target must name the port its service actually binds.
///
/// TranscriptService binds 50053. Every caller dialled 50053's neighbour, 50055 — which is
/// MeetingService's port, so the connection was refused on every attempt rather than landing
/// somewhere wrong and failing loudly. ArtifactsFinalizer caught that refusal and fell back to a
/// cache, and the fallback rendered an outage as "*No speech transcription recorded.*". The result
/// was 135 empty transcript exports out of 135 — every one ever produced — over meetings that had
/// as many as 405 stored segments, with a healthy service, a green deploy and no alert.
///
/// A unit test could not have caught it: both sides were internally consistent, and nothing in
/// process ever compared them. This does compare them, by reading the two sources of truth off
/// disk — the Kestrel binding in each service's Program.cs, and every URL configured anywhere in
/// the backend.
/// </summary>
public sealed class GrpcPortMapContractTests
{
    /// <summary>options.ListenAnyIP(50053, listenOptions => ... HttpProtocols.Http2)</summary>
    private static readonly Regex GrpcBinding = new(
        @"ListenAnyIP\(\s*(?<port>5\d{4})\s*,(?<body>[^;]*?)Http2",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>"TranscriptServiceUrl": "http://localhost:50053" — in JSON or in a C# fallback.</summary>
    private static readonly Regex ConfiguredTarget = new(
        @"(?<service>Auth|Workspace|TranslationRoom|Transcript|Billing|Notification|Meeting)Service(Grpc)?Url"
        + @"""?\s*[:=,]\s*""http://[^""/]+:(?<port>5\d{4})""",
        RegexOptions.Compiled);

    /// <summary>Directory name under warptalk-backend/ for each service in a *ServiceUrl key.</summary>
    private static readonly Dictionary<string, string> ServiceDirectories = new()
    {
        ["Auth"] = "auth",
        ["Workspace"] = "workspace",
        ["TranslationRoom"] = "translation-room",
        ["Transcript"] = "transcript",
        ["Billing"] = "billing",
        ["Notification"] = "notification",
        ["Meeting"] = "meeting",
    };

    [Fact]
    public void EveryConfiguredGrpcTargetMatchesThePortItsServiceBinds()
    {
        var root = FindBackendRoot();
        var bound = ReadBoundGrpcPorts(root);

        // Guards the guard: if the binding regex stops matching, every assertion below would
        // vacuously pass and this test would go quiet exactly when it mattered.
        Assert.True(
            bound.Count >= 5,
            $"Expected to find gRPC bindings for most services, found {bound.Count}: "
            + string.Join(", ", bound.Select(pair => $"{pair.Key}={pair.Value}")));
        Assert.Equal(50053, bound["transcript"]);

        var mismatches = new List<string>();
        var checkedTargets = 0;

        foreach (var file in ConfigurationFiles(root))
        {
            foreach (Match match in ConfiguredTarget.Matches(File.ReadAllText(file)))
            {
                var directory = ServiceDirectories[match.Groups["service"].Value];
                if (!bound.TryGetValue(directory, out var expected)) continue;

                checkedTargets++;
                var configured = int.Parse(match.Groups["port"].Value);
                if (configured != expected)
                {
                    mismatches.Add(
                        $"{Path.GetRelativePath(root, file)}: {match.Groups["service"].Value}Service "
                        + $"is dialled on {configured} but binds {expected}"
                        + (bound.FirstOrDefault(pair => pair.Value == configured) is { Key: not null } owner
                            ? $" ({configured} belongs to {owner.Key})"
                            : string.Empty));
                }
            }
        }

        Assert.True(checkedTargets >= 10, $"Only {checkedTargets} gRPC targets were checked.");
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    [Fact]
    public void NoTwoServicesBindTheSameGrpcPort()
    {
        // The mismatch above was survivable-looking because 50055 was a real port on a real
        // service. Two services sharing one would make a wrong target indistinguishable from a
        // right one.
        var bound = ReadBoundGrpcPorts(FindBackendRoot());

        var collisions = bound
            .GroupBy(pair => pair.Value)
            .Where(group => group.Count() > 1)
            .Select(group => $"port {group.Key}: {string.Join(", ", group.Select(pair => pair.Key))}")
            .ToList();

        Assert.True(collisions.Count == 0, string.Join("\n", collisions));
    }

    private static Dictionary<string, int> ReadBoundGrpcPorts(string root)
    {
        var ports = new Dictionary<string, int>();

        foreach (var program in Directory.EnumerateFiles(root, "Program.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(program) || !program.Contains(".API", StringComparison.Ordinal)) continue;

            var match = GrpcBinding.Match(File.ReadAllText(program));
            if (!match.Success) continue;

            var service = Path.GetRelativePath(root, program).Split(Path.DirectorySeparatorChar)[0];
            ports[service] = int.Parse(match.Groups["port"].Value);
        }

        return ports;
    }

    private static IEnumerable<string> ConfigurationFiles(string root)
    {
        foreach (var pattern in new[] { "appsettings*.json", "Program.cs" })
        {
            foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                if (!IsBuildOutput(file)) yield return file;
            }
        }
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string FindBackendRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "warptalk-backend.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate backend repository root.");
    }
}
