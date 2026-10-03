using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.API.Controllers;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Application.Services;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranscriptService.Tests;

/// <summary>
/// PO 2026-10-02: a glossary's language pair can be changed after creation. Owner/Admin only,
/// codes validated, and the existing terms are left exactly as they are.
/// </summary>
public class GlossaryLanguagePairUpdateTests
{
    private static readonly Guid GlossaryId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IGlossaryRepository _glossaries = Substitute.For<IGlossaryRepository>();
    private readonly IGlossaryTermRepository _terms = Substitute.For<IGlossaryTermRepository>();
    private readonly GlossaryService _service;
    private readonly Glossary _existing = new()
    {
        Id = GlossaryId,
        WorkspaceId = Guid.NewGuid(),
        Name = "IT Support",
        SourceLanguage = "en",
        TargetLanguage = "en",
        IsActive = true,
        UpdatedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    public GlossaryLanguagePairUpdateTests()
    {
        _unitOfWork.Glossaries.Returns(_glossaries);
        _unitOfWork.GlossaryTerms.Returns(_terms);
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase().ReturnsForAnyArgs(Substitute.For<IDatabase>());
        _service = new GlossaryService(_unitOfWork, Substitute.For<ILogger<GlossaryService>>(), redis);
        _glossaries.GetByIdAsync(GlossaryId, Arg.Any<CancellationToken>()).Returns(_existing);
    }

    [Fact]
    public async Task The_pair_changes_in_place_and_no_term_is_read_or_written()
    {
        var result = await _service.UpdateGlossaryLanguagesAsync(GlossaryId, new UpdateGlossaryLanguagesDto("EN-us", "vi-VN"));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("en", result.Value!.SourceLanguage);
        Assert.Equal("vi", result.Value.TargetLanguage);
        Assert.Equal("vi", _existing.TargetLanguage);
        Assert.True(_existing.UpdatedAt > new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        _glossaries.Received(1).Update(_existing);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Empty(_terms.ReceivedCalls());
    }

    [Theory]
    [InlineData("", "vi")]
    [InlineData("en", "  ")]
    [InlineData("english", "vi")]
    [InlineData("en", "v1")]
    public async Task A_code_that_is_not_a_language_is_refused_and_nothing_is_saved(string source, string target)
    {
        var result = await _service.UpdateGlossaryLanguagesAsync(GlossaryId, new UpdateGlossaryLanguagesDto(source, target));

        Assert.False(result.IsSuccess);
        Assert.Equal("BAD_REQUEST", result.ErrorCode);
        Assert.Equal("en", _existing.TargetLanguage);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task An_unknown_glossary_is_not_found()
    {
        var result = await _service.UpdateGlossaryLanguagesAsync(Guid.NewGuid(), new UpdateGlossaryLanguagesDto("en", "vi"));

        Assert.Equal("NOT_FOUND", result.ErrorCode);
    }

    // ── the endpoint's gate ────────────────────────────────────────────────────────────────

    private readonly IGlossaryService _glossaryService = Substitute.For<IGlossaryService>();
    private readonly IWorkspaceMembershipClient _membership = Substitute.For<IWorkspaceMembershipClient>();
    private readonly Guid _callerId = Guid.NewGuid();

    private GlossariesController Controller()
    {
        var controller = new GlossariesController(_glossaryService, Substitute.For<IGlobalGlossaryService>(), _membership)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, _callerId.ToString()) }, "test")),
                },
            },
        };
        _glossaryService.GetGlossaryByIdAsync(GlossaryId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GlossaryDto(GlossaryId, _existing.WorkspaceId, "IT Support", null, "en", "en", 3, true, DateTime.UtcNow, DateTime.UtcNow)));
        return controller;
    }

    [Fact]
    public async Task A_member_who_is_not_owner_or_admin_cannot_change_the_pair()
    {
        var controller = Controller();
        _membership.GetMembershipAsync(_existing.WorkspaceId, _callerId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMembership(IsMember: true, RoleName: "Member", IsActive: true));

        var result = await controller.UpdateGlossaryLanguages(GlossaryId, new UpdateGlossaryLanguagesDto("en", "vi"), CancellationToken.None);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        await _glossaryService.DidNotReceiveWithAnyArgs().UpdateGlossaryLanguagesAsync(default, default!, default);
    }

    [Fact]
    public async Task An_owner_changes_the_pair_and_gets_the_glossary_back()
    {
        var controller = Controller();
        _membership.GetMembershipAsync(_existing.WorkspaceId, _callerId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMembership(IsMember: true, RoleName: "Owner", IsActive: true));
        _glossaryService.UpdateGlossaryLanguagesAsync(GlossaryId, Arg.Any<UpdateGlossaryLanguagesDto>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GlossaryDto(GlossaryId, _existing.WorkspaceId, "IT Support", null, "en", "vi", 3, true, DateTime.UtcNow, DateTime.UtcNow)));

        var result = await controller.UpdateGlossaryLanguages(GlossaryId, new UpdateGlossaryLanguagesDto("en", "vi"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("vi", Assert.IsType<GlossaryDto>(ok.Value).TargetLanguage);
    }
}
