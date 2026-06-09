using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;
using WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class DocumentAiIngestionConsumerServiceTests
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceProvider _serviceProvider;
    private readonly IServiceScope _serviceScope;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IGenericRepository<WorkspaceDocument> _workspaceDocumentRepository;
    private readonly DocumentAiIngestionConsumerService _service;

    public DocumentAiIngestionConsumerServiceTests()
    {
        _redis = Substitute.For<IConnectionMultiplexer>();
        _serviceProvider = Substitute.For<IServiceProvider>();
        _serviceScope = Substitute.For<IServiceScope>();
        var serviceScopeFactory = Substitute.For<IServiceScopeFactory>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _workspaceRepository = Substitute.For<IWorkspaceRepository>();
        _workspaceDocumentRepository = Substitute.For<IGenericRepository<WorkspaceDocument>>();

        _serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(serviceScopeFactory);
        serviceScopeFactory.CreateScope().Returns(_serviceScope);
        _serviceScope.ServiceProvider.Returns(_serviceProvider);
        _serviceProvider.GetService(typeof(IUnitOfWork)).Returns(_unitOfWork);
        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);
        _unitOfWork.WorkspaceDocumentRepository.Returns(_workspaceDocumentRepository);

        _service = new DocumentAiIngestionConsumerService(
            _redis,
            Substitute.For<ILogger<DocumentAiIngestionConsumerService>>(),
            _serviceProvider
        );
    }

    [Fact]
    public async Task ProcessDocumentUploadAsync_ShouldMarkSensitiveAndNotEligible_WhenPIIDetected()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            FileName = "sensitive_test_pii.txt",
            IsSensitive = false,
            IngestionStatus = "pending",
            AiUsagePolicy = JsonSerializer.Serialize(new AiUsagePolicyConfiguration(
                AllowExternalLlm: true,
                RedactPii: new PiiRedactionConfiguration(Enabled: true),
                Dlp: new DlpConfiguration(Enabled: false, KeywordsBlacklist: null),
                TranslationProfile: null
            ))
        };

        _workspaceDocumentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);

        // Act
        await _service.ProcessDocumentUploadAsync(documentId, new Dictionary<string, string>(), CancellationToken.None);

        // Assert
        Assert.True(document.IsSensitive);
        Assert.Equal(WorkspaceDocumentConstants.SensitiveConfidentialityLevel, document.ConfidentialityLevel);
        Assert.False(document.AiEligible);
        Assert.Equal(WorkspaceDocumentIngestionStatus.completed.ToString(), document.IngestionStatus);
        _workspaceDocumentRepository.Received().Update(document);
    }

    [Fact]
    public async Task ProcessDocumentUploadAsync_ShouldMarkSensitiveAndNotEligible_WhenDlpKeywordDetected()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            FileName = "sensitive_test_dlp.txt",
            IsSensitive = false,
            IngestionStatus = "pending",
            AiUsagePolicy = JsonSerializer.Serialize(new AiUsagePolicyConfiguration(
                AllowExternalLlm: true,
                RedactPii: new PiiRedactionConfiguration(Enabled: false),
                Dlp: new DlpConfiguration(Enabled: true, KeywordsBlacklist: new List<string> { "doanh thu" }),
                TranslationProfile: null
            ))
        };

        _workspaceDocumentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);

        // Act
        await _service.ProcessDocumentUploadAsync(documentId, new Dictionary<string, string>(), CancellationToken.None);

        // Assert
        Assert.True(document.IsSensitive);
        Assert.Equal(WorkspaceDocumentConstants.SensitiveConfidentialityLevel, document.ConfidentialityLevel);
        Assert.False(document.AiEligible);
        Assert.Equal(WorkspaceDocumentIngestionStatus.completed.ToString(), document.IngestionStatus);
    }

    [Fact]
    public async Task ProcessDocumentUploadAsync_ShouldFallbackToWorkspaceSettings_WhenDocumentPolicyIsNull()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            FileName = "sensitive_test_dlp.txt",
            IsSensitive = false,
            IngestionStatus = "pending",
            AiUsagePolicy = null // No document policy
        };

        var wsConfig = new WorkspaceConfiguration
        {
            AiUsagePolicy = new AiUsagePolicyConfiguration(
                AllowExternalLlm: true,
                RedactPii: new PiiRedactionConfiguration(Enabled: false),
                Dlp: new DlpConfiguration(Enabled: true, KeywordsBlacklist: new List<string> { "doanh thu" }),
                TranslationProfile: null
            )
        };

        var workspace = new Workspace
        {
            Id = workspaceId,
            Settings = JsonSerializer.Serialize(wsConfig)
        };

        _workspaceDocumentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);

        // Act
        await _service.ProcessDocumentUploadAsync(documentId, new Dictionary<string, string>(), CancellationToken.None);

        // Assert
        Assert.True(document.IsSensitive);
        Assert.Equal(WorkspaceDocumentConstants.SensitiveConfidentialityLevel, document.ConfidentialityLevel);
        Assert.False(document.AiEligible);
        Assert.Equal(WorkspaceDocumentIngestionStatus.completed.ToString(), document.IngestionStatus);
    }

    [Fact]
    public async Task ProcessDocumentUploadAsync_ShouldFallbackToFailSafe_WhenExceptionIsThrown()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        _workspaceDocumentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkspaceDocument?>(new Exception("Database connection failed")));

        // Act
        // Should not crash due to fail-safe try-catch
        await _service.ProcessDocumentUploadAsync(documentId, new Dictionary<string, string>(), CancellationToken.None);

        // Assert
        // Verified it gracefully logs/handles it and applies safety fallback if document was loaded
        // (Since document retrieval itself failed here, it skips updating, but verify no exception is bubbled up)
    }
}
