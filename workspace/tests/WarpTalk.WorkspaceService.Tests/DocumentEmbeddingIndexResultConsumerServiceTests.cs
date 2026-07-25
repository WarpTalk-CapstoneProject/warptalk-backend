using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Services;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class DocumentEmbeddingIndexResultConsumerServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IGenericRepository<WorkspaceDocument> _documentRepository;
    private readonly IGenericRepository<WorkspaceDocumentAudit> _auditRepository;
    private readonly DocumentEmbeddingResultProcessor _processor;

    public DocumentEmbeddingIndexResultConsumerServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _documentRepository = Substitute.For<IGenericRepository<WorkspaceDocument>>();
        _auditRepository = Substitute.For<IGenericRepository<WorkspaceDocumentAudit>>();

        _unitOfWork.WorkspaceDocumentRepository.Returns(_documentRepository);
        _unitOfWork.WorkspaceDocumentAuditRepository.Returns(_auditRepository);

        _processor = new DocumentEmbeddingResultProcessor(
            _unitOfWork,
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
}
