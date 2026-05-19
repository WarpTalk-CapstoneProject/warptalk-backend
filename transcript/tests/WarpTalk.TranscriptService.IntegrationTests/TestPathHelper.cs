using System;
using System.IO;

namespace WarpTalk.TranscriptService.IntegrationTests;

internal static class TestPathHelper
{
    public static string FindTranscriptRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var marker = Path.Combine(dir.FullName, "WarpTalk.TranscriptService.slnx");
            if (File.Exists(marker))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate transcript root from test runtime path.");
    }

    public static string FindWorkspaceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var exportsDbml = Path.Combine(dir.FullName, "exports", "warptalk-schema-updated.dbml");
            if (File.Exists(exportsDbml))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate workspace root containing exports/warptalk-schema-updated.dbml.");
    }
}
