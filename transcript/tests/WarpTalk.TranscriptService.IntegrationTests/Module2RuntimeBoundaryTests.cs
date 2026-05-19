using System.IO;
using Xunit;

namespace WarpTalk.TranscriptService.IntegrationTests;

public class Module2RuntimeBoundaryTests
{
    [Fact]
    public void TranscriptStatusEnum_DoesNotPersistTransientPipelineStages()
    {
        var transcriptRoot = TestPathHelper.FindTranscriptRoot();
        var enumPath = Path.Combine(transcriptRoot, "src", "WarpTalk.TranscriptService.Domain", "Enums", "TranscriptStatus.cs");
        var enumContent = File.ReadAllText(enumPath);

        Assert.DoesNotContain("BUFFERING", enumContent);
        Assert.DoesNotContain("STT_PROCESSING", enumContent);
        Assert.DoesNotContain("TRANSLATION_PROCESSING", enumContent);
        Assert.DoesNotContain("BROADCASTING", enumContent);
        Assert.DoesNotContain("RETRYING", enumContent);
    }

    [Fact]
    public void WorkspaceSchemaAndStateDiagramReferencesExist()
    {
        var workspaceRoot = TestPathHelper.FindWorkspaceRoot();

        var dbmlPath = Path.Combine(workspaceRoot, "exports", "warptalk-schema-updated.dbml");
        var statePath = Path.Combine(workspaceRoot, "exports", "warptalk-module-2-state-diagrams.md");
        var flowPath = Path.Combine(workspaceRoot, "warptalk-backend", "specs", "069-module-2-integration-tests-api-docs", "integration-flow.md");

        Assert.True(File.Exists(dbmlPath), $"Missing schema export: {dbmlPath}");
        Assert.True(File.Exists(statePath), $"Missing state diagram export: {statePath}");
        Assert.True(File.Exists(flowPath), $"Missing WT-78 flow coverage doc: {flowPath}");

        var flowContent = File.ReadAllText(flowPath);
        Assert.Contains("Capture speech chunk enters runtime pipeline", flowContent);
        Assert.Contains("Transcript segment persistence", flowContent);
        Assert.Contains("Translation result processing writes translated output", flowContent);
        Assert.Contains("not transient runtime stage transitions", flowContent);
    }
}
