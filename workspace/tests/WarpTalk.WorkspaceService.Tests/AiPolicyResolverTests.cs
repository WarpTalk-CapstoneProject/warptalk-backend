using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;
using WarpTalk.WorkspaceService.Infrastructure.Services;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class AiPolicyResolverTests
{
    [Fact]
    public async Task ResolvePolicySettingsAsync_ShouldAlwaysAllowExternalLlm_WhenPoliciesDisableIt()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            AiUsagePolicy = JsonSerializer.Serialize(new AiUsagePolicyConfiguration(
                AllowExternalLlm: false,
                RedactPii: new PiiRedactionConfiguration(Enabled: true),
                Dlp: null,
                TranslationProfile: null))
        };

        var workspace = new Workspace
        {
            Id = workspaceId,
            Settings = JsonSerializer.Serialize(new WorkspaceConfiguration
            {
                AiUsagePolicy = new AiUsagePolicyConfiguration(
                    AllowExternalLlm: false,
                    RedactPii: null,
                    Dlp: new DlpConfiguration(Enabled: true, KeywordsBlacklist: new List<string> { "secret" }),
                    TranslationProfile: null)
            })
        };

        var workspaceRepository = Substitute.For<IWorkspaceRepository>();
        workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.WorkspaceRepository.Returns(workspaceRepository);

        var resolver = new AiPolicyResolver(Substitute.For<ILogger<AiPolicyResolver>>());

        // Act
        var result = await resolver.ResolvePolicySettingsAsync(unitOfWork, document);

        // Assert
        Assert.True(result.AllowExternalLlm);
        Assert.True(result.PiiEnabled);
        Assert.True(result.DlpEnabled);
        Assert.NotNull(result.KeywordsBlacklist);
        Assert.Contains("secret", result.KeywordsBlacklist);
    }
}
