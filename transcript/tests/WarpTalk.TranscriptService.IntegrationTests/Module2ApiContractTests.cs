using System.IO;
using Xunit;

namespace WarpTalk.TranscriptService.IntegrationTests;

public class Module2ApiContractTests
{
    [Fact]
    public void ApiRealtimeContractDoc_CoversMainModule2Routes()
    {
        var transcriptRoot = TestPathHelper.FindTranscriptRoot();
        var docPath = Path.Combine(transcriptRoot, "docs", "module-2-api-realtime-contract.md");

        Assert.True(File.Exists(docPath), $"Missing contract doc: {docPath}");

        var content = File.ReadAllText(docPath);

        Assert.Contains("GET /api/v1/transcripts/{id}", content);
        Assert.Contains("GET /api/v1/transcripts/by-room/{translationRoomId}", content);
        Assert.Contains("GET /api/v1/transcripts/{transcriptId}/segments", content);
        Assert.Contains("GET /api/v1/transcripts/{transcriptId}/translations", content);
        Assert.Contains("POST /api/v1/transcripts/{transcriptId}/segments/{segmentId}/correct", content);
        Assert.Contains("POST /api/v1/transcripts/{transcriptId}/exports", content);
        Assert.Contains("GET /api/v1/transcripts/{transcriptId}/exports/{id}/download", content);
        Assert.Contains("stt:results:{roomId}", content);
        Assert.Contains("translate:results:{roomId}", content);
    }

    [Fact]
    public void ControllersExposeExpectedModule2Routes()
    {
        var transcriptRoot = TestPathHelper.FindTranscriptRoot();
        var controllersRoot = Path.Combine(transcriptRoot, "src", "WarpTalk.TranscriptService.API", "Controllers");

        var transcriptsController = File.ReadAllText(Path.Combine(controllersRoot, "TranscriptsController.cs"));
        var segmentsController = File.ReadAllText(Path.Combine(controllersRoot, "TranscriptSegmentsController.cs"));
        var translationsController = File.ReadAllText(Path.Combine(controllersRoot, "TranscriptTranslationsController.cs"));
        var correctionsController = File.ReadAllText(Path.Combine(controllersRoot, "TranscriptCorrectionsController.cs"));
        var exportsController = File.ReadAllText(Path.Combine(controllersRoot, "TranscriptExportsController.cs"));

        Assert.Contains("[Route(\"api/v1/transcripts\")]", transcriptsController);
        Assert.Contains("[Route(\"api/v1/transcripts/{transcriptId}/segments\")]", segmentsController);
        Assert.Contains("[Route(\"api/v1/transcripts/{transcriptId}/translations\")]", translationsController);
        Assert.Contains("[Route(\"api/v1/transcripts/{transcriptId}/segments/{segmentId}\")]", correctionsController);
        Assert.Contains("[Route(\"correct\")]", correctionsController);
        Assert.Contains("[Route(\"api/v1/transcripts/{transcriptId}/exports\")]", exportsController);
    }
}
