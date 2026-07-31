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
        _documentRepository.Received().Update(document);
        await _auditRepository.Received().AddAsync(Arg.Any<WorkspaceDocumentAudit>(), Arg.Any<CancellationToken>());
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
