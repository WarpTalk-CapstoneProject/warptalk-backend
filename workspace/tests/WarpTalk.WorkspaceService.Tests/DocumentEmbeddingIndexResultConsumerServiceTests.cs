using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Adapters;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class DocumentEmbeddingIndexResultConsumerServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceDocumentRepository _documentRepository;
    private readonly IWorkspaceDocumentAuditRepository _auditRepository;
    private readonly IWorkspaceDocumentEventPublisher _eventPublisher;
    private readonly DocumentEmbeddingResultProcessor _processor;

    public DocumentEmbeddingIndexResultConsumerServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _documentRepository = Substitute.For<IWorkspaceDocumentRepository>();
        _auditRepository = Substitute.For<IWorkspaceDocumentAuditRepository>();
        _eventPublisher = Substitute.For<IWorkspaceDocumentEventPublisher>();

        _unitOfWork.WorkspaceDocumentRepository.Returns(_documentRepository);
        _unitOfWork.WorkspaceDocumentAuditRepository.Returns(_auditRepository);

        _processor = new DocumentEmbeddingResultProcessor(
            _unitOfWork,
            _eventPublisher,
            Substitute.For<ILogger<DocumentEmbeddingResultProcessor>>());
    }

    [Fact]
    public async Task ProcessEmbeddingResultAsync_ShouldMarkDocumentCompleted_WhenStatusIsIndexed()
    {
        var documentId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            IsAiAllowed = true,
            AiEligible = true,
            ConfidentialityLevel = "public_internal",
            Status = WorkspaceDocumentStatus.@public.ToString(),
            IngestionStatus = WorkspaceDocumentIngestionStatus.processing.ToString()
        };
        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);

        await _processor.ProcessResultAsync(new Dictionary<string, string>
        {
            ["job_id"] = "job-1",
            ["source_id"] = documentId.ToString(),
            ["status"] = "indexed",
            ["chunks_indexed"] = "3",
            ["provider"] = "openai",
            ["model"] = "text-embedding-3-small",
            ["dimensions"] = "1536"
        }, CancellationToken.None);

        Assert.Equal(WorkspaceDocumentIngestionStatus.completed.ToString(), document.IngestionStatus);
        Assert.True(document.AiEligible);
        Assert.NotNull(document.LastIndexedAt);
        Assert.Equal("openai/text-embedding-3-small/1536", document.IndexVersion);
        _documentRepository.Received().Update(document);
        await _auditRepository.Received().AddAsync(Arg.Any<WorkspaceDocumentAudit>(), Arg.Any<CancellationToken>());
        await _eventPublisher.Received(1).PublishDocumentLifecycleAsync(
            documentId,
            workspaceId,
            WorkspaceDocumentStatus.@public.ToString(),
            WorkspaceDocumentIngestionStatus.completed.ToString(),
            WorkspaceDocumentConstants.LifecycleEvents.Completed,
            Arg.Any<DateTime>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessEmbeddingResultAsync_ShouldFailDocument_WhenEmbeddingFailed()
    {
        var documentId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            IsAiAllowed = true,
            AiEligible = true,
            ConfidentialityLevel = "public_internal",
            Status = WorkspaceDocumentStatus.@public.ToString(),
            IngestionStatus = WorkspaceDocumentIngestionStatus.processing.ToString()
        };
        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);

        await _processor.ProcessResultAsync(new Dictionary<string, string>
        {
            ["job_id"] = "job-2",
            ["source_id"] = documentId.ToString(),
            ["status"] = "failed",
            ["provider"] = "openai",
            ["model"] = "text-embedding-3-small",
            ["dimensions"] = "1536",
            ["reason"] = "qdrant unavailable"
        }, CancellationToken.None);

        Assert.Equal(WorkspaceDocumentIngestionStatus.failed.ToString(), document.IngestionStatus);
        Assert.False(document.AiEligible);
        Assert.Null(document.LastIndexedAt);
        // WT-428. This assertion is the whole point: six production documents read "AI Failed"
        // with ingestion_failure_reason NULL, because WT-411 gave the guardrail's branches a
        // reason and left this one — the only other writer of 'failed' — writing nothing. A
        // failure with no recorded cause cannot be triaged or retried on evidence.
        Assert.Equal(
            WorkspaceDocumentIngestionFailureReasons.EmbeddingFailed,
            document.IngestionFailureReason);
        _documentRepository.Received().Update(document);
        await _auditRepository.Received().AddAsync(Arg.Any<WorkspaceDocumentAudit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessEmbeddingResultAsync_ShouldRecordBlockedReason_WhenWorkerRefuses()
    {
        // A refusal and a fault are different facts pointing at different components, and both
        // used to land as the same reason-less row.
        var documentId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = Guid.NewGuid(),
            IsAiAllowed = true,
            AiEligible = true,
            ConfidentialityLevel = "public_internal",
            Status = WorkspaceDocumentStatus.@public.ToString(),
            IngestionStatus = WorkspaceDocumentIngestionStatus.processing.ToString()
        };
        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);

        await _processor.ProcessResultAsync(new Dictionary<string, string>
        {
            ["job_id"] = "job-3",
            ["source_id"] = documentId.ToString(),
            ["status"] = "blocked",
            ["reason"] = "policy"
        }, CancellationToken.None);

        Assert.Equal(WorkspaceDocumentIngestionStatus.skipped.ToString(), document.IngestionStatus);
        Assert.False(document.AiEligible);
        Assert.Equal(
            WorkspaceDocumentIngestionFailureReasons.EmbeddingBlocked,
            document.IngestionFailureReason);
    }

    [Fact]
    public async Task ProcessEmbeddingResultAsync_ShouldClearReason_WhenIndexingLaterSucceeds()
    {
        // A stale reason must not outlive the failure it described — the same rule the guardrail
        // applies on a clean pass. Without this, a document that failed once and then indexed
        // fine would keep explaining a failure that no longer exists.
        var documentId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = Guid.NewGuid(),
            IsAiAllowed = true,
            AiEligible = false,
            ConfidentialityLevel = "public_internal",
            Status = WorkspaceDocumentStatus.@public.ToString(),
            IngestionStatus = WorkspaceDocumentIngestionStatus.failed.ToString(),
            IngestionFailureReason = WorkspaceDocumentIngestionFailureReasons.EmbeddingFailed
        };
        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);

        await _processor.ProcessResultAsync(new Dictionary<string, string>
        {
            ["job_id"] = "job-4",
            ["source_id"] = documentId.ToString(),
            ["status"] = "indexed",
            ["provider"] = "openai",
            ["model"] = "text-embedding-3-small",
            ["dimensions"] = "1536"
        }, CancellationToken.None);

        Assert.Equal(WorkspaceDocumentIngestionStatus.completed.ToString(), document.IngestionStatus);
        Assert.True(document.AiEligible);
        Assert.Null(document.IngestionFailureReason);
    }

    [Theory]
    [InlineData("transcript")]
    [InlineData("meeting_summary")]
    [InlineData("global_glossary_term")]
    public async Task ProcessEmbeddingResultAsync_ShouldIgnore_ResultsThatAreNotDocuments(string sourceType)
    {
        // embedding:index_results is shared, and this group is its only consumer — so a
        // transcript's result arrives here too. It must be dropped without a database lookup:
        // production had 209 such entries and every one produced a "document not found" warning.
        await _processor.ProcessResultAsync(new Dictionary<string, string>
        {
            ["job_id"] = "job-x",
            ["source_type"] = sourceType,
            ["source_id"] = Guid.NewGuid().ToString(),
            ["status"] = "indexed"
        }, CancellationToken.None);

        await _documentRepository.DidNotReceive()
            .GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _documentRepository.DidNotReceive().Update(Arg.Any<WorkspaceDocument>());
    }

    [Fact]
    public async Task ProcessEmbeddingResultAsync_ShouldStillHandle_AResultWithNoSourceType()
    {
        // Every producer stamps source_type today. Treating a missing one as foreign would
        // silently drop a real document result to tidy up logs — the wrong way round.
        var documentId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = Guid.NewGuid(),
            IsAiAllowed = true,
            ConfidentialityLevel = "public_internal",
            Status = WorkspaceDocumentStatus.@public.ToString(),
            IngestionStatus = WorkspaceDocumentIngestionStatus.processing.ToString()
        };
        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);

        await _processor.ProcessResultAsync(new Dictionary<string, string>
        {
            ["job_id"] = "job-y",
            ["source_id"] = documentId.ToString(),
            ["status"] = "indexed",
            ["provider"] = "openai",
            ["model"] = "text-embedding-3-small",
            ["dimensions"] = "1536"
        }, CancellationToken.None);

        Assert.Equal(WorkspaceDocumentIngestionStatus.completed.ToString(), document.IngestionStatus);
    }

    [Fact]
    public async Task ProcessEmbeddingResultAsync_ShouldNotReenableAi_WhenDocumentWasRejected()
    {
        var documentId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            IsAiAllowed = true,
            AiEligible = false,
            ConfidentialityLevel = "public_internal",
            Status = WorkspaceDocumentStatus.rejected.ToString(),
            IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString()
        };
        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);

        await _processor.ProcessResultAsync(new Dictionary<string, string>
        {
            ["job_id"] = "late-job",
            ["source_id"] = documentId.ToString(),
            ["status"] = "indexed",
            ["provider"] = "openai",
            ["model"] = "text-embedding-3-small",
            ["dimensions"] = "1536"
        }, CancellationToken.None);

        Assert.False(document.AiEligible);
        Assert.Equal(WorkspaceDocumentStatus.rejected.ToString(), document.Status);
    }
}
