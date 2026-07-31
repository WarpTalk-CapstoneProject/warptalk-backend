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
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Interfaces.Caching;
using AppModelRole = WarpTalk.WorkspaceService.Application.Models.Role;
using WarpTalk.WorkspaceService.Application.Models;
using AppWorkspaceService = WarpTalk.WorkspaceService.Application.Services.WorkspaceService;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;
using WarpTalk.WorkspaceService.Infrastructure.Adapters;
using WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class DlpSecurityGuardrailE2ETests
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceProvider _serviceProvider;
    private readonly IServiceScope _serviceScope;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkspaceDocumentRepository _workspaceDocumentRepository;
    private readonly IWorkspaceMemberRepository _workspaceMemberRepository;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly IWorkspaceCacheService _workspaceCache;
    private readonly IWorkspaceEventPublisher _eventPublisher;
    private readonly IWorkspaceDocumentStorage _storage;
    private readonly IDocumentTextExtractor _textExtractor;
    private readonly IDocumentSecurityScanner _securityScanner;
    private readonly IWorkspaceDocumentEventPublisher _docEventPublisher;
    private readonly IEmbeddingIndexPublisher _embeddingPublisher;
    private readonly IAiPolicyResolver _policyResolver;
    private readonly IDatabase _database;

    private readonly AppWorkspaceService _workspaceService;
    private readonly DocumentSecurityGuardrailConsumerService _consumerService;

    public DlpSecurityGuardrailE2ETests()
    {
        _redis = Substitute.For<IConnectionMultiplexer>();
        _serviceProvider = Substitute.For<IServiceProvider>();
        _serviceScope = Substitute.For<IServiceScope>();
        var serviceScopeFactory = Substitute.For<IServiceScopeFactory>();

        _unitOfWork = Substitute.For<IUnitOfWork>();
        _workspaceRepository = Substitute.For<IWorkspaceRepository>();
        _workspaceDocumentRepository = Substitute.For<IWorkspaceDocumentRepository>();
        _workspaceMemberRepository = Substitute.For<IWorkspaceMemberRepository>();
        _authIdentity = Substitute.For<IAuthIdentityClient>();
        _workspaceCache = Substitute.For<IWorkspaceCacheService>();
        _eventPublisher = Substitute.For<IWorkspaceEventPublisher>();
        _storage = Substitute.For<IWorkspaceDocumentStorage>();
        _textExtractor = Substitute.For<IDocumentTextExtractor>();
        _securityScanner = Substitute.For<IDocumentSecurityScanner>();
        _docEventPublisher = Substitute.For<IWorkspaceDocumentEventPublisher>();
        _embeddingPublisher = Substitute.For<IEmbeddingIndexPublisher>();
        _policyResolver = new AiPolicyResolver(Substitute.For<ILogger<AiPolicyResolver>>());
        _database = Substitute.For<IDatabase>();

        _redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(_database);
        _serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(serviceScopeFactory);
        serviceScopeFactory.CreateScope().Returns(_serviceScope);
        _serviceScope.ServiceProvider.Returns(_serviceProvider);

        _serviceProvider.GetService(typeof(IUnitOfWork)).Returns(_unitOfWork);
        _serviceProvider.GetService(typeof(IWorkspaceDocumentStorage)).Returns(_storage);
        _serviceProvider.GetService(typeof(IDocumentTextExtractor)).Returns(_textExtractor);
        _serviceProvider.GetService(typeof(IDocumentSecurityScanner)).Returns(_securityScanner);
        _serviceProvider.GetService(typeof(IWorkspaceDocumentEventPublisher)).Returns(_docEventPublisher);
        _serviceProvider.GetService(typeof(IAiPolicyResolver)).Returns(_policyResolver);
        _serviceProvider.GetService(typeof(IEmbeddingIndexPublisher)).Returns(_embeddingPublisher);

        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);
        _unitOfWork.WorkspaceDocumentRepository.Returns(_workspaceDocumentRepository);
        _unitOfWork.WorkspaceMemberRepository.Returns(_workspaceMemberRepository);

        _workspaceService = new AppWorkspaceService(
            _unitOfWork,
            _workspaceCache,
            Substitute.For<ILogger<AppWorkspaceService>>(),
            _authIdentity,
            _eventPublisher
        );

        _consumerService = new DocumentSecurityGuardrailConsumerService(
            _redis,
            Substitute.For<ILogger<DocumentSecurityGuardrailConsumerService>>(),
            _serviceProvider
        );
    }

    [Fact]
    public async Task E2E_DlpBlacklistKeyword_ShouldUpdateIngestionStatusToSkipped_And_RestrictedConfidentiality()
    {
        // 1. Arrange Workspace & Admin Role
        var workspaceId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        var workspace = new Workspace
        {
            Id = workspaceId,
            Name = "Enterprise R&D",
            Slug = "enterprise-rd",
            Settings = "{}"
        };

        var adminMember = new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = adminUserId,
            RoleId = adminRoleId
        };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<System.Linq.Expressions.Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(adminMember);
        _authIdentity.GetRoleByIdAsync(adminRoleId, Arg.Any<CancellationToken>())
            .Returns(new AppModelRole { Id = adminRoleId, Name = "Owner" });

        // Capture saved DB settings
        WorkspaceConfiguration? savedConfig = null;
        _workspaceRepository.UpdateSettingsAsync(workspaceId, Arg.Do<WorkspaceConfiguration>(c => savedConfig = c), adminUserId, Arg.Any<CancellationToken>())
            .Returns(true);

        // 2. Act Step 1: Admin configures DLP Blacklist Keywords in Workspace Settings
        var blacklistKeywords = new List<string> { "Project-Alpha-Secret", "Restricted-Finance" };
        var settingsDto = new WorkspaceSettingsDto(
            "en",
            "UTC",
            new List<string>(),
            true,
            5,
            30,
            true,
            new List<string>(),
            true,
            true,
            new AiUsagePolicyDto(
                true,
                new PiiRedactionDto(true),
                new DlpDto(true, blacklistKeywords),
                new TranslationProfileDto("professional", new LanguageSpecificRulesDto("formal_hierarchical", "keigo_teineigo"))
            ),
            false
        );

        var updateResult = await _workspaceService.UpdateWorkspaceSettingsAsync(workspaceId, settingsDto, adminUserId);
        Assert.True(updateResult.IsSuccess);
        Assert.NotNull(savedConfig);
        Assert.True(savedConfig!.AiUsagePolicy!.Dlp!.Enabled);
        Assert.NotNull(savedConfig.AiUsagePolicy.Dlp.KeywordsBlacklist);
        Assert.Contains("Project-Alpha-Secret", savedConfig.AiUsagePolicy.Dlp.KeywordsBlacklist);

        // Update workspace in mock repository with saved settings
        workspace.Settings = JsonSerializer.Serialize(savedConfig);

        // 3. Act Step 2: Upload document containing a DLP blacklisted keyword
        var documentId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            FileName = "project_alpha_roadmap.pdf",
            FileExtension = ".pdf",
            ConfidentialityLevel = WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel,
            IsAiAllowed = true,
            Status = WorkspaceDocumentStatus.@public.ToString(),
            RetentionState = "active",
            IngestionStatus = WorkspaceDocumentIngestionStatus.pending.ToString()
        };

        _workspaceDocumentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);

        var rawTextWithBlacklistedKeyword = "This document details confidential specs for Project-Alpha-Secret architecture.";
        var contentStream = new MemoryStream(Encoding.UTF8.GetBytes(rawTextWithBlacklistedKeyword));
        _storage.GetDecryptedStreamAsync(document, Arg.Any<CancellationToken>()).Returns(contentStream);

        _textExtractor.ExtractTextAsync(Arg.Any<Stream>(), ".pdf", Arg.Any<CancellationToken>())
            .Returns(new ExtractedDocumentContent { FullText = rawTextWithBlacklistedKeyword });

        _securityScanner.ScanAsync(
                rawTextWithBlacklistedKeyword,
                true,
                true,
                Arg.Is<List<string>>(list => list.Contains("Project-Alpha-Secret")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DocumentSecurityScanResult(
                ViolationFound: true,
                PiiDetected: false,
                DlpDetected: true,
                MaskedContent: rawTextWithBlacklistedKeyword
            )));

        // 4. Act Step 3: Security Worker processes uploaded document
        var handled = await _consumerService.ProcessDocumentUploadAsync(documentId, new Dictionary<string, string>(), CancellationToken.None);

        // 5. Assert Step 4: Verify end-to-end security guardrail mutations
        Assert.True(handled);
        Assert.Equal(WorkspaceDocumentConstants.SensitiveConfidentialityLevel, document.ConfidentialityLevel);
        Assert.False(document.AiEligible);
        Assert.Equal(WorkspaceDocumentIngestionStatus.skipped.ToString(), document.IngestionStatus);

        // Verify document embedding request was NOT published due to DLP violation
        await _embeddingPublisher.DidNotReceive().PublishEmbeddingIndexRequestAsync(
            Arg.Any<WorkspaceDocument>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task E2E_CleanDocument_WithoutBlacklistKeyword_ShouldProceedToProcessing_And_Embedding()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var wsConfig = new WorkspaceConfiguration
        {
            AiUsagePolicy = new AiUsagePolicyConfiguration(
                AllowExternalLlm: true,
                RedactPii: new PiiRedactionConfiguration(Enabled: true),
                Dlp: new DlpConfiguration(Enabled: true, KeywordsBlacklist: new List<string> { "Project-Alpha-Secret" }),
                TranslationProfile: null
            )
        };

        var workspace = new Workspace
        {
            Id = workspaceId,
            Settings = JsonSerializer.Serialize(wsConfig)
        };

        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            FileName = "public_overview.pdf",
            FileExtension = ".pdf",
            ConfidentialityLevel = WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel,
            IsAiAllowed = true,
            Status = WorkspaceDocumentStatus.@public.ToString(),
            RetentionState = "active",
            IngestionStatus = WorkspaceDocumentIngestionStatus.pending.ToString()
        };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceDocumentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);

        var cleanText = "This is a public overview document with standard corporate information.";
        var contentStream = new MemoryStream(Encoding.UTF8.GetBytes(cleanText));
        _storage.GetDecryptedStreamAsync(document, Arg.Any<CancellationToken>()).Returns(contentStream);

        _textExtractor.ExtractTextAsync(Arg.Any<Stream>(), ".pdf", Arg.Any<CancellationToken>())
            .Returns(new ExtractedDocumentContent { FullText = cleanText });

        _securityScanner.ScanAsync(
                cleanText,
                true,
                true,
                Arg.Is<List<string>>(list => list.Contains("Project-Alpha-Secret")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DocumentSecurityScanResult(
                ViolationFound: false,
                PiiDetected: false,
                DlpDetected: false,
                MaskedContent: cleanText
            )));

        _embeddingPublisher.PublishEmbeddingIndexRequestAsync(
                Arg.Any<WorkspaceDocument>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns("job-123");

        // Act
        var handled = await _consumerService.ProcessDocumentUploadAsync(documentId, new Dictionary<string, string>(), CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel, document.ConfidentialityLevel);
        Assert.Equal(WorkspaceDocumentIngestionStatus.processing.ToString(), document.IngestionStatus);

        // Verify embedding request WAS published
        await _embeddingPublisher.Received(1).PublishEmbeddingIndexRequestAsync(
            document, cleanText, true, Arg.Any<CancellationToken>());
    }
}
