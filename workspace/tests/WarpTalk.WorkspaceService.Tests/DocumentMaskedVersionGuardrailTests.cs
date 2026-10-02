using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;
using WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// The guardrail's side of the masked copy: it is produced when a scan answers, recorded on the
/// scan's audit row, and re-produced — for a restricted document, and for that alone — when an
/// Owner/Admin asks.
/// </summary>
public class DocumentMaskedVersionGuardrailTests
{
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IWorkspaceDocumentRepository _documents = Substitute.For<IWorkspaceDocumentRepository>();
    private readonly IWorkspaceDocumentAuditRepository _audits = Substitute.For<IWorkspaceDocumentAuditRepository>();
    private readonly IWorkspaceDocumentStorage _storage = Substitute.For<IWorkspaceDocumentStorage>();
    private readonly IDocumentTextExtractor _textExtractor = Substitute.For<IDocumentTextExtractor>();
    private readonly IDocumentSecurityScanner _scanner = Substitute.For<IDocumentSecurityScanner>();
    private readonly IEmbeddingIndexPublisher _embeddingPublisher = Substitute.For<IEmbeddingIndexPublisher>();
    private readonly IWorkspaceDocumentEventPublisher _lifecycle = Substitute.For<IWorkspaceDocumentEventPublisher>();
    private readonly IDocumentMaskedVersionGenerator _maskedVersions = Substitute.For<IDocumentMaskedVersionGenerator>();
    private readonly List<WorkspaceDocumentAudit> _written = new();
    private readonly DocumentSecurityGuardrailConsumerService _service;

    private const string RawText = "Owner Nguyễn Văn A, mobile 0912345678";
    private const string MaskedText = "Owner [PII_REDACTED], mobile [PHONE_REDACTED]";

    public DocumentMaskedVersionGuardrailTests()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var scope = Substitute.For<IServiceScope>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        _serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(scopeFactory);
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(_serviceProvider);
        _serviceProvider.GetService(typeof(IUnitOfWork)).Returns(_unitOfWork);
        _serviceProvider.GetService(typeof(IWorkspaceDocumentStorage)).Returns(_storage);
        _serviceProvider.GetService(typeof(IDocumentTextExtractor)).Returns(_textExtractor);
        _serviceProvider.GetService(typeof(IDocumentSecurityScanner)).Returns(_scanner);
        _serviceProvider.GetService(typeof(IWorkspaceDocumentEventPublisher)).Returns(_lifecycle);
        _serviceProvider.GetService(typeof(IAiPolicyResolver)).Returns(
            new WarpTalk.WorkspaceService.Infrastructure.Adapters.AiPolicyResolver(
                Substitute.For<ILogger<WarpTalk.WorkspaceService.Infrastructure.Adapters.AiPolicyResolver>>()));
        _serviceProvider.GetService(typeof(IEmbeddingIndexPublisher)).Returns(_embeddingPublisher);
        _serviceProvider.GetService(typeof(IDocumentMaskedVersionGenerator)).Returns(_maskedVersions);

        _unitOfWork.WorkspaceDocumentRepository.Returns(_documents);
        _unitOfWork.WorkspaceDocumentAuditRepository.Returns(_audits);
        _unitOfWork.WorkspaceRepository.Returns(Substitute.For<IWorkspaceRepository>());
        _audits.AddAsync(Arg.Do<WorkspaceDocumentAudit>(_written.Add), Arg.Any<CancellationToken>());

        _embeddingPublisher.PublishEmbeddingIndexRequestAsync(
                Arg.Any<WorkspaceDocument>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns("embedding-job");

        _storage.GetDecryptedStreamAsync(Arg.Any<WorkspaceDocument>(), Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes(RawText)));
        _textExtractor.ExtractTextAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractedDocumentContent { FullText = RawText });
        _maskedVersions.GenerateAsync(
                Arg.Any<WorkspaceDocument>(), Arg.Any<byte[]>(), Arg.Any<ExtractedDocumentContent>(),
                Arg.Any<DocumentSecurityScanResult>(), Arg.Any<CancellationToken>())
            .Returns(WorkspaceDocumentMaskedVersionStatuses.Available);

        _service = new DocumentSecurityGuardrailConsumerService(
            redis,
            Substitute.For<ILogger<DocumentSecurityGuardrailConsumerService>>(),
            _serviceProvider);
    }

    private WorkspaceDocument Document(string confidentiality)
    {
        var document = new WorkspaceDocument
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            FileName = "staff.md",
            FileExtension = ".md",
            StorageKey = "documents/ws/doc.md",
            ConfidentialityLevel = confidentiality,
            IsAiAllowed = true,
            Status = WorkspaceDocumentStatus.@public.ToString(),
            RetentionState = "active",
            IngestionStatus = WorkspaceDocumentIngestionStatus.pending.ToString(),
            AiUsagePolicy = JsonSerializer.Serialize(new AiUsagePolicyConfiguration(
                AllowExternalLlm: true,
                RedactPii: new PiiRedactionConfiguration(Enabled: true),
                Dlp: new DlpConfiguration(Enabled: false, KeywordsBlacklist: null),
                TranslationProfile: null))
        };
        _documents.GetByIdAsync(document.Id, Arg.Any<CancellationToken>()).Returns(document);
        return document;
    }

    private void RescanRequested(WorkspaceDocument document, DateTime at) =>
        _audits.GetLatestActionAsync(document.Id, WorkspaceDocumentConstants.AuditActions.MaskedVersionRescanRequested, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceDocumentAudit { DocumentId = document.Id, ActionAt = at });

    private static string? MaskedVersionOf(WorkspaceDocumentAudit audit)
    {
        using var parsed = JsonDocument.Parse(audit.Metadata!);
        return parsed.RootElement.TryGetProperty("MaskedVersion", out var value) ? value.GetString() : null;
    }

    [Fact]
    public async Task AScanThatFindsPii_ProducesTheMaskedCopy_FromTheFileAndTheScanResult_AndRecordsTheOutcome()
    {
        var document = Document("internal");
        var scan = new DocumentSecurityScanResult(true, true, false, MaskedText);
        _scanner.ScanAsync(RawText, true, false, null, Arg.Any<CancellationToken>()).Returns(scan);

        await _service.ProcessDocumentUploadAsync(document.Id, new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal(WorkspaceDocumentConstants.SensitiveConfidentialityLevel, document.ConfidentialityLevel);
        await _maskedVersions.Received(1).GenerateAsync(
            document,
            Arg.Is<byte[]>(bytes => Encoding.UTF8.GetString(bytes) == RawText),
            Arg.Is<ExtractedDocumentContent>(c => c.FullText == RawText),
            scan,
            Arg.Any<CancellationToken>());

        var audit = Assert.Single(_written, a => a.Action == WorkspaceDocumentConstants.AuditActions.SecurityScanCompleted);
        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.Available, MaskedVersionOf(audit));
        // The audit row says what happened. It never carries the text.
        Assert.DoesNotContain("0912345678", audit.Metadata);
        Assert.DoesNotContain("Nguyễn", audit.Metadata);
    }

    [Fact]
    public async Task AScanThatThrows_RemovesAnyMaskedCopy_AndStillFailsClosed()
    {
        var document = Document("internal");
        _scanner.ScanAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("scan timed out"));

        var handled = await _service.ProcessDocumentUploadAsync(document.Id, new Dictionary<string, string>(), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(WorkspaceDocumentConstants.SensitiveConfidentialityLevel, document.ConfidentialityLevel);
        await _maskedVersions.Received(1).DiscardAsync(document, Arg.Any<CancellationToken>());
        await _maskedVersions.DidNotReceiveWithAnyArgs().GenerateAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task ARestrictedDocument_WithNoRescanRequest_IsSkippedAsBefore()
    {
        var document = Document(WorkspaceDocumentConstants.SensitiveConfidentialityLevel);

        await _service.ProcessDocumentUploadAsync(document.Id, new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal(WorkspaceDocumentIngestionStatus.skipped.ToString(), document.IngestionStatus);
        await _scanner.DidNotReceiveWithAnyArgs().ScanAsync(default!, default, default, default, default);
        await _maskedVersions.DidNotReceiveWithAnyArgs().GenerateAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task ARescanRequest_ScansARestrictedDocument_ForTheMaskedCopyAlone()
    {
        var document = Document(WorkspaceDocumentConstants.SensitiveConfidentialityLevel);
        document.IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString();
        RescanRequested(document, DateTime.UtcNow.AddMinutes(-1));
        var scan = new DocumentSecurityScanResult(true, true, false, MaskedText);
        // PII detection is forced on for a re-scan, whatever the policy says.
        _scanner.ScanAsync(RawText, true, false, null, Arg.Any<CancellationToken>()).Returns(scan);

        var handled = await _service.ProcessDocumentUploadAsync(document.Id, new Dictionary<string, string>(), CancellationToken.None);

        Assert.True(handled);
        await _maskedVersions.Received(1).GenerateAsync(document, Arg.Any<byte[]>(), Arg.Any<ExtractedDocumentContent>(), scan, Arg.Any<CancellationToken>());

        // Nothing about the document itself moves: still restricted, still not indexed.
        Assert.Equal(WorkspaceDocumentConstants.SensitiveConfidentialityLevel, document.ConfidentialityLevel);
        Assert.Equal(WorkspaceDocumentIngestionStatus.skipped.ToString(), document.IngestionStatus);
        Assert.False(document.AiEligible);
        await _embeddingPublisher.DidNotReceiveWithAnyArgs().PublishEmbeddingIndexRequestAsync(default!, default!, default, default);

        var audit = Assert.Single(_written, a => a.Action == WorkspaceDocumentConstants.AuditActions.SecurityScanCompleted);
        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.Available, MaskedVersionOf(audit));
        await _lifecycle.Received(1).PublishDocumentLifecycleAsync(
            document.Id, document.WorkspaceId, document.Status, document.IngestionStatus,
            WorkspaceDocumentConstants.LifecycleEvents.Updated, Arg.Any<DateTime>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARescanRequest_AlreadyAnsweredByALaterScan_IsNotRunAgain()
    {
        var document = Document(WorkspaceDocumentConstants.SensitiveConfidentialityLevel);
        RescanRequested(document, DateTime.UtcNow.AddMinutes(-10));
        _audits.GetLatestActionAsync(document.Id, WorkspaceDocumentConstants.AuditActions.SecurityScanCompleted, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceDocumentAudit { DocumentId = document.Id, ActionAt = DateTime.UtcNow.AddMinutes(-5) });

        await _service.ProcessDocumentUploadAsync(document.Id, new Dictionary<string, string>(), CancellationToken.None);

        await _scanner.DidNotReceiveWithAnyArgs().ScanAsync(default!, default, default, default, default);
    }

    [Fact]
    public async Task ARescanThatFails_RecordsTheFailure_RemovesAnyCopy_AndLeavesTheDocumentAlone()
    {
        var document = Document(WorkspaceDocumentConstants.SensitiveConfidentialityLevel);
        document.IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString();
        RescanRequested(document, DateTime.UtcNow.AddMinutes(-1));
        _scanner.ScanAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("worker could not complete the scan"));

        var handled = await _service.ProcessDocumentUploadAsync(document.Id, new Dictionary<string, string>(), CancellationToken.None);

        Assert.True(handled);
        Assert.Contains(_written, a => a.Action == WorkspaceDocumentConstants.AuditActions.MaskedVersionRescanFailed);
        Assert.DoesNotContain(_written, a => a.Action == WorkspaceDocumentConstants.AuditActions.SecurityScanCompleted);
        await _maskedVersions.Received(1).DiscardAsync(document, Arg.Any<CancellationToken>());
        Assert.Equal(WorkspaceDocumentIngestionStatus.skipped.ToString(), document.IngestionStatus);
        Assert.Null(document.IngestionFailureReason);
    }
}
