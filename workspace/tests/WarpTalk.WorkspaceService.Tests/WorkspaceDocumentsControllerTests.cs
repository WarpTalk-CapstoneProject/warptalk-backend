using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.API.Controllers;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;
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

    /// <summary>An upload whose bytes do not matter — the service is mocked.</summary>
    private static IFormFile StubUpload()
    {
        var file = Substitute.For<IFormFile>();
        file.FileName.Returns("plan.pdf");
        file.Length.Returns(1024);
        return file;
    }

    private static WorkspaceDocumentDto StubDocument(Guid id) => new(
        id, Guid.NewGuid(), null, null, null,
        "Quarterly plan", "plan.pdf", ".pdf", "application/pdf", 1024,
        "upload", null, "skipped", false, true,
        "public_internal", "active", "public", null,
        DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public async Task UploadDocument_ShouldReturn409AndNameTheDuplicate_WhenTheBytesAreAlreadyHere()
    {
        var workspaceId = Guid.NewGuid();
        var existingId = Guid.NewGuid();
        var request = new UploadDocumentApiRequest("Quarterly plan", "upload", null, null, StubUpload());

        _documentService.UploadDocumentAsync(workspaceId, request, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new UploadDocumentOutcomeDto(
                UploadDocumentOutcomeDto.Duplicate,
                null,
                new DocumentDuplicateDto(existingId, "The original", "plan.pdf", "public", 1024, DateTime.UtcNow))));

        var result = await _controller.UploadDocument(workspaceId, request, CancellationToken.None);

        // THE WIRE CONTRACT THE WEB BRANCHES ON. A duplicate is a successful service call whose
        // answer is a question, so the service reports it as an outcome and the 409 is built here —
        // with the colliding document attached, because the three choices WT-666 offers are
        // unanswerable without knowing which document they are about.
        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var body = Assert.IsType<DocumentDuplicateConflictResponse>(conflict.Value);
        Assert.Equal(ErrorCodes.DocumentDuplicateContent, body.Code);
        Assert.Equal(existingId, body.Duplicate!.DocumentId);
        Assert.Contains("The original", body.Error);
    }

    [Fact]
    public async Task UploadDocument_ShouldWithholdTheName_WhenTheCallerCannotOpenTheDuplicate()
    {
        var workspaceId = Guid.NewGuid();
        var request = new UploadDocumentApiRequest("Quarterly plan", "upload", null, null, StubUpload());

        _documentService.UploadDocumentAsync(workspaceId, request, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new UploadDocumentOutcomeDto(UploadDocumentOutcomeDto.Duplicate, null, null)));

        var result = await _controller.UploadDocument(workspaceId, request, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var body = Assert.IsType<DocumentDuplicateConflictResponse>(conflict.Value);
        Assert.Equal(ErrorCodes.DocumentDuplicateContent, body.Code);
        // Still a conflict, and still says nothing about a document this person may not open.
        Assert.Null(body.Duplicate);
        Assert.DoesNotContain("\"", body.Error);
    }

    [Theory]
    [InlineData("created")]
    [InlineData("skipped")]
    [InlineData("replaced")]
    public async Task UploadDocument_ShouldReturnTheDocumentItself_OnEveryOutcomeThatStoredOne(string outcome)
    {
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var request = new UploadDocumentApiRequest("Quarterly plan", "upload", null, null, StubUpload());
        var document = StubDocument(documentId);

        _documentService.UploadDocumentAsync(workspaceId, request, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new UploadDocumentOutcomeDto(outcome, document)));

        var result = await _controller.UploadDocument(workspaceId, request, CancellationToken.None);

        // The success body is UNCHANGED by this work: the document, not the envelope around it.
        // `skip` returns the document that was kept and `replace` the one that was updated, so a
        // caller that ignores the outcome still gets a usable answer.
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<WorkspaceDocumentDto>(ok.Value);
        Assert.Equal(documentId, body.Id);
    }

    [Fact]
    public async Task UploadDocument_ShouldReturn400_WhenTheFileIsNotWhatItClaims()
    {
        var workspaceId = Guid.NewGuid();
        var request = new UploadDocumentApiRequest("Quarterly plan", "upload", null, null, StubUpload());

        _documentService.UploadDocumentAsync(workspaceId, request, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<UploadDocumentOutcomeDto>(
                "This file's contents do not match its .pdf extension.", ErrorCodes.ValidationError));

        var result = await _controller.UploadDocument(workspaceId, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ApiErrorResponse>(badRequest.Value);
        Assert.Equal(ErrorCodes.ValidationError, error.Code);
    }
}
