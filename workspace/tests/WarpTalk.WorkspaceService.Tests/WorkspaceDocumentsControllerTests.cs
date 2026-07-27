using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.API.Controllers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceDocumentsControllerTests
{
    private readonly IWorkspaceDocumentService _documentService;
    private readonly WorkspaceDocumentsController _controller;
    private readonly Guid _userId;

    public WorkspaceDocumentsControllerTests()
    {
        _documentService = Substitute.For<IWorkspaceDocumentService>();
        _controller = new WorkspaceDocumentsController(_documentService);
        _userId = Guid.NewGuid();

        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, _userId.ToString()) };
        var identity = new ClaimsIdentity(claims, "TestAuth");

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    [Fact]
    public async Task DeleteDocument_ShouldReturn404_WhenDocumentDoesNotExist()
    {
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        _documentService.DeleteDocumentAsync(workspaceId, documentId, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Document not found.", ErrorCodes.NotFound));

        var result = await _controller.DeleteDocument(workspaceId, documentId, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var error = Assert.IsType<ApiErrorResponse>(notFound.Value);
        Assert.Equal(ErrorCodes.NotFound, error.Code);
    }

    [Fact]
    public async Task DeleteDocument_ShouldReturn403_WhenUserCannotDelete()
    {
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        _documentService.DeleteDocumentAsync(workspaceId, documentId, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Forbidden.", ErrorCodes.Forbidden));

        var result = await _controller.DeleteDocument(workspaceId, documentId, CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var error = Assert.IsType<ApiErrorResponse>(forbidden.Value);
        Assert.Equal(ErrorCodes.Forbidden, error.Code);
    }

    [Fact]
    public async Task DeleteDocument_ShouldReturn500_WhenServiceFailsUnexpectedly()
    {
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        _documentService.DeleteDocumentAsync(workspaceId, documentId, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Unexpected error.", ErrorCodes.InternalServerError));

        var result = await _controller.DeleteDocument(workspaceId, documentId, CancellationToken.None);

        var serverError = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, serverError.StatusCode);
        var error = Assert.IsType<ApiErrorResponse>(serverError.Value);
        Assert.Equal(ErrorCodes.InternalServerError, error.Code);
    }
}
