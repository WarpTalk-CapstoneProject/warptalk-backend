using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.API.Controllers;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;
using Xunit;

namespace WarpTalk.TranscriptService.Tests;

/// <summary>
/// A workspace-scoped id in the route or body used to be trusted outright: any authenticated
/// user of the platform, whatever workspace they actually belonged to, could read and write
/// another workspace's glossary terms just by knowing or guessing a glossary/workspace id.
/// These tests pin the fix — every action must ask IWorkspaceMembershipClient before touching
/// IGlossaryService, and refuse with 403 when the caller doesn't belong there.
/// </summary>
public class GlossariesControllerAuthorizationTests
{
    private readonly IGlossaryService _glossaryService = Substitute.For<IGlossaryService>();
    private readonly IGlobalGlossaryService _globalGlossaryService = Substitute.For<IGlobalGlossaryService>();
    private readonly IWorkspaceMembershipClient _membershipClient = Substitute.For<IWorkspaceMembershipClient>();
    private readonly Guid _callerId = Guid.NewGuid();
    private readonly Guid _foreignWorkspaceId = Guid.NewGuid();

    private GlossariesController CreateController()
    {
        var controller = new GlossariesController(_glossaryService, _globalGlossaryService, _membershipClient);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, _callerId.ToString()) }, "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user },
        };
        return controller;
    }

    private static GlossaryDto MakeGlossary(Guid workspaceId) => new(
        Id: Guid.NewGuid(),
        WorkspaceId: workspaceId,
        Name: "Test",
        Description: null,
        SourceLanguage: "en",
        TargetLanguage: "vi",
        TermCount: 0,
        IsActive: true,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow);

    [Fact]
    public async Task GetGlossariesByWorkspace_Returns403_WhenCallerIsNotAMember()
    {
        _membershipClient.GetMembershipAsync(_foreignWorkspaceId, _callerId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMembership(IsMember: false, RoleName: "", IsActive: false));

        var result = await CreateController().GetGlossariesByWorkspace(_foreignWorkspaceId, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, status.StatusCode);
        await _glossaryService.DidNotReceiveWithAnyArgs().GetGlossariesByWorkspaceIdAsync(default, default);
    }

    [Fact]
    public async Task GetGlossariesByWorkspace_Succeeds_WhenCallerIsAnActiveMember()
    {
        _membershipClient.GetMembershipAsync(_foreignWorkspaceId, _callerId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMembership(IsMember: true, RoleName: "Member", IsActive: true));
        _glossaryService.GetGlossariesByWorkspaceIdAsync(_foreignWorkspaceId, Arg.Any<CancellationToken>())
            .Returns(Result.Success<IEnumerable<GlossaryDto>>(new[] { MakeGlossary(_foreignWorkspaceId) }));

        var result = await CreateController().GetGlossariesByWorkspace(_foreignWorkspaceId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetTerms_Returns403_ForAGlossaryInAWorkspaceTheCallerDoesNotBelongTo()
    {
        var glossaryId = Guid.NewGuid();
        _glossaryService.GetGlossaryByIdAsync(glossaryId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(MakeGlossary(_foreignWorkspaceId)));
        _membershipClient.GetMembershipAsync(_foreignWorkspaceId, _callerId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMembership(IsMember: false, RoleName: "", IsActive: false));

        var result = await CreateController().GetTerms(glossaryId, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, status.StatusCode);
        await _glossaryService.DidNotReceiveWithAnyArgs().GetTermsByGlossaryIdAsync(default, default);
    }

    [Fact]
    public async Task AddTerm_Returns403_WhenCallerIsAMemberButNotOwnerOrAdmin()
    {
        // A Member belongs to the workspace and can read it, but glossary CRUD is Owner/Admin
        // only — this must be refused even though EnsureMemberAsync would have let it through.
        var glossaryId = Guid.NewGuid();
        _glossaryService.GetGlossaryByIdAsync(glossaryId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(MakeGlossary(_foreignWorkspaceId)));
        _membershipClient.GetMembershipAsync(_foreignWorkspaceId, _callerId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMembership(IsMember: true, RoleName: "Member", IsActive: true));

        var result = await CreateController().AddTerm(
            glossaryId,
            new CreateGlossaryTermDto("hello", "xin chao", null, null, null, null, null),
            CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, status.StatusCode);
        await _glossaryService.DidNotReceiveWithAnyArgs().AddTermAsync(default, default!, default);
    }

    [Fact]
    public async Task AddTerm_Succeeds_WhenCallerIsOwner()
    {
        var glossaryId = Guid.NewGuid();
        _glossaryService.GetGlossaryByIdAsync(glossaryId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(MakeGlossary(_foreignWorkspaceId)));
        _membershipClient.GetMembershipAsync(_foreignWorkspaceId, _callerId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMembership(IsMember: true, RoleName: "Owner", IsActive: true));
        _glossaryService.AddTermAsync(glossaryId, Arg.Any<CreateGlossaryTermDto>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await CreateController().AddTerm(
            glossaryId,
            new CreateGlossaryTermDto("hello", "xin chao", null, null, null, null, null),
            CancellationToken.None);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(201, status.StatusCode);
    }
}
