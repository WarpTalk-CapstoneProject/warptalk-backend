using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.API.Controllers;

[ApiController]
[Route("api/v1/workspaces/{workspaceId:guid}/documents")]
public class WorkspaceDocumentsController : ControllerBase
{
    private readonly IWorkspaceDocumentService _documentService;

    public WorkspaceDocumentsController(IWorkspaceDocumentService documentService)
    {
        _documentService = documentService;
    }

    [Authorize]
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(10 * 1024 * 1024)] // Enforce 10MB limit at request level
    public async Task<IActionResult> UploadDocument(
        Guid workspaceId,
        [FromForm] UploadDocumentApiRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        if (request.File == null || request.File.Length == 0)
        {
            return BadRequest(new ApiErrorResponse("No file was uploaded.", ErrorCodes.ValidationError));
        }

        var result = await _documentService.UploadDocumentAsync(workspaceId, request, userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> ListDocuments(
        Guid workspaceId,
        [FromQuery] GetDocumentsQuery query,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _documentService.ListDocumentsAsync(workspaceId, query, userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpGet("{documentId:guid}")]
    public async Task<IActionResult> GetDocumentById(
        Guid workspaceId,
        Guid documentId,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _documentService.GetDocumentByIdAsync(workspaceId, documentId, userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpPatch("{documentId:guid}")]
    public async Task<IActionResult> PatchDocumentMetadata(
        Guid workspaceId,
        Guid documentId,
        [FromBody] PatchDocumentRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _documentService.PatchDocumentMetadataAsync(workspaceId, documentId, request, userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpPost("{documentId:guid}/approve")]
    public async Task<IActionResult> ApproveDocument(
        Guid workspaceId,
        Guid documentId,
        [FromBody] ApproveDocumentRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _documentService.ApproveDocumentAsync(workspaceId, documentId, request, userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return NoContent();
    }

    [Authorize]
    [HttpGet("{documentId:guid}/download")]
    public async Task<IActionResult> DownloadDocument(
        Guid workspaceId,
        Guid documentId,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _documentService.DownloadDocumentAsync(workspaceId, documentId, userId.Value, ct);
        if (!result.IsSuccess || result.Value == null)
        {
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error ?? "Download failed.", result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error ?? "Download failed.", result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error ?? "Download failed.", result.ErrorCode));
        }

        var dto = result.Value;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(dto.Stream, dto.ContentType, dto.FileName);
    }

    [Authorize]
    [HttpGet("{documentId:guid}/extracted-text")]
    public async Task<IActionResult> GetExtractedText(
        Guid workspaceId,
        Guid documentId,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _documentService.GetExtractedTextAsync(workspaceId, documentId, userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpPut("{documentId:guid}/extracted-text")]
    public async Task<IActionResult> UpdateExtractedText(
        Guid workspaceId,
        Guid documentId,
        [FromBody] UpdateExtractedTextRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _documentService.UpdateExtractedTextAsync(workspaceId, documentId, request.Text, userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpDelete("{documentId:guid}")]
    public async Task<IActionResult> DeleteDocument(
        Guid workspaceId,
        Guid documentId,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _documentService.DeleteDocumentAsync(workspaceId, documentId, userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return NoContent();
    }

    [Authorize]
    [HttpPost("{documentId:guid}/policies")]
    public async Task<IActionResult> AddAccessPolicy(
        Guid workspaceId,
        Guid documentId,
        [FromBody] AddAccessPolicyRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _documentService.AddAccessPolicyAsync(workspaceId, documentId, request, userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return NoContent();
    }

    [Authorize]
    [HttpDelete("{documentId:guid}/policies/{policyId:guid}")]
    public async Task<IActionResult> RemoveAccessPolicy(
        Guid workspaceId,
        Guid documentId,
        Guid policyId,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _documentService.RemoveAccessPolicyAsync(workspaceId, documentId, policyId, userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return NoContent();
    }

    [Authorize]
    [HttpGet("{documentId:guid}/policies")]
    public async Task<IActionResult> GetAccessPolicies(
        Guid workspaceId,
        Guid documentId,
        [FromQuery] GetWorkspacesQuery query,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _documentService.GetAccessPoliciesAsync(workspaceId, documentId, query, userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpPost("{documentId:guid}/archive")]
    public async Task<IActionResult> ArchiveDocument(
        Guid workspaceId,
        Guid documentId,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _documentService.ArchiveDocumentAsync(workspaceId, documentId, userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return NoContent();
    }

    [Authorize]
    [HttpPost("{documentId:guid}/restore")]
    public async Task<IActionResult> RestoreDocument(
        Guid workspaceId,
        Guid documentId,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _documentService.RestoreDocumentAsync(workspaceId, documentId, userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return NoContent();
    }
}
