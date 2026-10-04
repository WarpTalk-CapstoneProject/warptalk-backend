using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Infrastructure.Adapters;
using WarpTalk.WorkspaceService.Infrastructure.Masking;
using Xunit;
using static WarpTalk.WorkspaceService.Tests.MaskedDocumentFixtures;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// From a finished scan to a stored masked copy — or to nothing. Every path that does not end in
/// "available" must end with no copy in storage.
/// </summary>
public class DocumentMaskedVersionGeneratorTests
{
    private const string Name = "Nguyễn Văn A";
    private const string Phone = "0912345678";

    private readonly IWorkspaceDocumentStorage _storage = Substitute.For<IWorkspaceDocumentStorage>();
    private readonly IDocumentSecurityScanner _scanner = Substitute.For<IDocumentSecurityScanner>();
    private readonly IMaskedPdfRenderer _pdfRenderer = Substitute.For<IMaskedPdfRenderer>();
    private readonly DocumentTextExtractor _realExtractor = new();
    private byte[]? _stored;

    public DocumentMaskedVersionGeneratorTests()
    {
        _storage.SaveMaskedFileAsync(Arg.Any<WorkspaceDocument>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                using var copy = new MemoryStream();
                call.Arg<Stream>().CopyTo(copy);
                _stored = copy.ToArray();
                return Task.CompletedTask;
            });

        // Nothing outside the body is personal unless a test says so.
        _scanner.ScanAsync(Arg.Any<string>(), true, false, null, Arg.Any<CancellationToken>())
            .Returns(call => new DocumentSecurityScanResult(false, false, false, call.Arg<string>()));
    }

    private DocumentMaskedVersionGenerator Generator(IDocumentTextExtractor? extractor = null) => new(
        _storage,
        extractor ?? _realExtractor,
        _scanner,
        _pdfRenderer,
        Substitute.For<ILogger<DocumentMaskedVersionGenerator>>());

    private static WorkspaceDocument Document(string extension) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        Name = "Staff list",
        FileName = "staff" + extension,
        FileExtension = extension,
        StorageKey = "documents/ws/doc" + extension
    };

    private async Task<ExtractedDocumentContent> ExtractAsync(byte[] file, string extension)
    {
        using var stream = new MemoryStream(file, writable: false);
        return await _realExtractor.ExtractTextAsync(stream, extension);
    }

    private static DocumentSecurityScanResult Pii(string maskedContent) => new(true, true, false, maskedContent);

    private async Task AssertNothingStoredAsync()
    {
        Assert.Null(_stored);
        await _storage.DidNotReceiveWithAnyArgs().SaveMaskedFileAsync(default!, default!, default);
        await _storage.Received(1).DeleteMaskedFileAsync(Arg.Any<WorkspaceDocument>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Docx_StoresAMaskedDocx_WithTheValuesGone()
    {
        var docx = Docx(new OpenXmlElement[]
        {
            Paragraph("Contact Ngu", "yễn Văn A"),
            Table(new[] { "Phone" }, new[] { Phone })
        });
        var content = await ExtractAsync(docx, ".docx");
        var scan = Pii(content.FullText.Replace(Name, "[PII_REDACTED]").Replace(Phone, "[PHONE_REDACTED]"));

        var status = await Generator().GenerateAsync(Document(".docx"), docx, content, scan);

        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.Available, status);
        Assert.NotNull(_stored);
        var masked = (await ExtractAsync(_stored!, ".docx")).FullText;
        Assert.DoesNotContain(Name, masked);
        Assert.DoesNotContain(Phone, masked);
        Assert.Contains("[PII_REDACTED]", masked);
        Assert.Contains("[PHONE_REDACTED]", masked);
        await _storage.DidNotReceiveWithAnyArgs().DeleteMaskedFileAsync(default!, default);
    }

    [Fact]
    public async Task Docx_TextTheFirstScanNeverSaw_IsScannedToo_AndHidden()
    {
        var header = $"Prepared by {Name}";
        var docx = Docx(new OpenXmlElement[] { Paragraph($"Call {Phone}") }, header: header);
        var content = await ExtractAsync(docx, ".docx");
        Assert.DoesNotContain(Name, content.FullText);

        _scanner.ScanAsync(Arg.Is<string>(text => text.Contains(header)), true, false, null, Arg.Any<CancellationToken>())
            .Returns(call => new DocumentSecurityScanResult(true, true, false, call.Arg<string>().Replace(Name, "[PII_REDACTED]")));

        var status = await Generator().GenerateAsync(
            Document(".docx"), docx, content, Pii(content.FullText.Replace(Phone, "[PHONE_REDACTED]")));

        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.Available, status);
        var everything = string.Join("\n", OpenXmlPiiMasker.CollectText(_stored!, ".docx", OpenXmlPiiMasker.TextScope.Everything));
        Assert.DoesNotContain(Name, everything);
        Assert.Contains("Prepared by [PII_REDACTED]", everything);
    }

    [Fact]
    public async Task Xlsx_StoresAMaskedWorkbook()
    {
        var xlsx = Xlsx("Staff", new object[] { "Name", "Phone" }, new object[] { Name, Phone });
        var content = await ExtractAsync(xlsx, ".xlsx");
        var scan = Pii(content.FullText.Replace(Name, "[PII_REDACTED]").Replace(Phone, "[PHONE_REDACTED]"));

        var status = await Generator().GenerateAsync(Document(".xlsx"), xlsx, content, scan);

        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.Available, status);
        var masked = (await ExtractAsync(_stored!, ".xlsx")).FullText;
        // Two markers with only a space between them are one hole in the scanned text, so both
        // cells carry the generic marker. What matters is that each cell lost its own half.
        Assert.Contains("[PII_REDACTED] [PII_REDACTED]", masked);
        Assert.DoesNotContain(Phone, masked);
        Assert.DoesNotContain(Name, masked);
        Assert.DoesNotContain(
            Name,
            string.Join("\n", OpenXmlPiiMasker.CollectText(_stored!, ".xlsx", OpenXmlPiiMasker.TextScope.Everything)));
    }

    [Fact]
    public async Task Markdown_StoresTheSameTextWithTheValuesReplaced()
    {
        var text = $"# Notes\n\nOwner: {Name}\nMobile: {Phone}\n";
        var file = Encoding.UTF8.GetBytes(text);
        var content = await ExtractAsync(file, ".md");

        var status = await Generator().GenerateAsync(
            Document(".md"), file, content, Pii(text.Replace(Name, "[PII_REDACTED]").Replace(Phone, "[PHONE_REDACTED]")));

        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.Available, status);
        Assert.Equal("# Notes\n\nOwner: [PII_REDACTED]\nMobile: [PHONE_REDACTED]\n", Encoding.UTF8.GetString(_stored!));
    }

    [Fact]
    public async Task Pdf_TakesTheRenderPath_AndStoresWhatTheRendererReturns()
    {
        var rendered = Encoding.ASCII.GetBytes("%PDF-1.7 rendered-from-masked-text");
        byte[]? layout = null;
        _pdfRenderer.IsConfigured.Returns(true);
        _pdfRenderer.RenderFromDocxAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                layout = call.Arg<byte[]>();
                return rendered;
            });

        var content = new ExtractedDocumentContent
        {
            FullText = $"Page one for {Name}\nPage two, call {Phone}\n",
            Pages = new List<ExtractedPage>
            {
                new() { PageNumber = 1, Text = $"Page one for {Name}" },
                new() { PageNumber = 2, Text = $"Page two, call {Phone}" }
            }
        };
        var scan = Pii("Page one for [PII_REDACTED]\nPage two, call [PHONE_REDACTED]\n");

        var status = await Generator().GenerateAsync(
            Document(".pdf"), Encoding.ASCII.GetBytes("%PDF-1.4 original"), content, scan);

        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.Available, status);
        Assert.Equal(rendered, _stored);

        // What was sent to the converter is the MASKED text, laid out as a Word file.
        Assert.NotNull(layout);
        var sent = (await ExtractAsync(layout!, ".docx")).FullText;
        Assert.Contains("Page one for [PII_REDACTED]", sent);
        Assert.Contains("Page two, call [PHONE_REDACTED]", sent);
        Assert.DoesNotContain(Name, sent);
        Assert.DoesNotContain(Phone, sent);
    }

    [Fact]
    public async Task Pdf_WithNoConverter_GetsNoMaskedCopy()
    {
        _pdfRenderer.IsConfigured.Returns(false);
        var content = new ExtractedDocumentContent
        {
            FullText = $"Call {Phone}\n",
            Pages = new List<ExtractedPage> { new() { PageNumber = 1, Text = $"Call {Phone}" } }
        };

        var status = await Generator().GenerateAsync(
            Document(".pdf"), new byte[] { 1 }, content, Pii("Call [PHONE_REDACTED]\n"));

        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.ConverterUnavailable, status);
        await AssertNothingStoredAsync();
        await _pdfRenderer.DidNotReceiveWithAnyArgs().RenderFromDocxAsync(default!, default);
    }

    [Fact]
    public async Task Pdf_WithNoText_IsAScannedPage_AndGetsNoMaskedCopy()
    {
        _pdfRenderer.IsConfigured.Returns(true);
        var content = new ExtractedDocumentContent
        {
            FullText = "x",
            Pages = new List<ExtractedPage> { new() { PageNumber = 1, Text = "  " } }
        };

        var status = await Generator().GenerateAsync(Document(".pdf"), new byte[] { 1 }, content, Pii("[PII_REDACTED]"));

        Assert.NotEqual(WorkspaceDocumentMaskedVersionStatuses.Available, status);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task VerificationFailure_StoresNothing_AndRemovesAnyEarlierCopy()
    {
        var docx = Docx(new OpenXmlElement[] { Paragraph($"Call {Phone}") });
        var content = await ExtractAsync(docx, ".docx");

        // An extractor that still reads the value out of the masked file: the stand-in for any
        // place the rewrite did not reach.
        var leaky = Substitute.For<IDocumentTextExtractor>();
        leaky.ExtractTextAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractedDocumentContent { FullText = $"Call {Phone}" });

        var status = await Generator(leaky).GenerateAsync(
            Document(".docx"), docx, content, Pii(content.FullText.Replace(Phone, "[PHONE_REDACTED]")));

        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.VerificationFailed, status);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task VerificationFailure_OnAStructuredPatternTheScanMissed_StoresNothing()
    {
        // The scan hid the name and overlooked an email. The masked file is otherwise fine —
        // and is thrown away, because the worker's own fast path would have masked that email.
        var text = $"Owner: {Name}, mail other.person@example.com";
        var file = Encoding.UTF8.GetBytes(text);
        var content = await ExtractAsync(file, ".md");

        var status = await Generator().GenerateAsync(
            Document(".md"), file, content, Pii(text.Replace(Name, "[PII_REDACTED]")));

        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.VerificationFailed, status);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task AlignmentFailure_StoresNothing()
    {
        var text = $"Owner: {Name}";
        var file = Encoding.UTF8.GetBytes(text);
        var content = await ExtractAsync(file, ".md");

        var status = await Generator().GenerateAsync(
            Document(".md"), file, content, Pii("Something else entirely happened to [PII_REDACTED] in this reply"));

        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.AlignmentFailed, status);
        await AssertNothingStoredAsync();
    }

    [Theory]
    [InlineData(false, true, WorkspaceDocumentMaskedVersionStatuses.DlpBlocked)]
    [InlineData(true, true, WorkspaceDocumentMaskedVersionStatuses.DlpBlocked)]
    [InlineData(false, false, WorkspaceDocumentMaskedVersionStatuses.NoPiiFound)]
    public async Task NoMaskedCopy_WhenThereIsNothingToHide_OrABannedKeyword(bool pii, bool dlp, string expected)
    {
        var file = Encoding.UTF8.GetBytes("plain text");
        var content = await ExtractAsync(file, ".md");

        var status = await Generator().GenerateAsync(
            Document(".md"), file, content, new DocumentSecurityScanResult(pii || dlp, pii, dlp, "plain [PII_REDACTED]"));

        Assert.Equal(expected, status);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task PiiReportedWithoutMaskedText_StoresNothing()
    {
        var text = $"Owner: {Name}";
        var file = Encoding.UTF8.GetBytes(text);
        var content = await ExtractAsync(file, ".md");

        var status = await Generator().GenerateAsync(Document(".md"), file, content, Pii(text));

        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.MaskedTextUnavailable, status);
        await AssertNothingStoredAsync();
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    public async Task Images_AreNeverMasked(string extension)
    {
        var content = new ExtractedDocumentContent { FullText = $"scanned id of {Name}" };

        var status = await Generator().GenerateAsync(
            Document(extension), new byte[] { 0x89, 0x50 }, content, Pii("scanned id of [PII_REDACTED]"));

        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.UnsupportedFormat, status);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task ACorruptFile_IsAnError_NotAnException_AndStoresNothing()
    {
        var content = new ExtractedDocumentContent { FullText = $"Call {Phone}" };

        var status = await Generator().GenerateAsync(
            Document(".docx"), Encoding.ASCII.GetBytes("this is not a zip"), content, Pii("Call [PHONE_REDACTED]"));

        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.Error, status);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task AStoreThatRefusesTheCopy_IsAnError()
    {
        _storage.SaveMaskedFileAsync(Arg.Any<WorkspaceDocument>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("disk full"));
        var text = $"Mobile: {Phone}";
        var file = Encoding.UTF8.GetBytes(text);
        var content = await ExtractAsync(file, ".md");

        var status = await Generator().GenerateAsync(Document(".md"), file, content, Pii("Mobile: [PHONE_REDACTED]"));

        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.Error, status);
    }
}
