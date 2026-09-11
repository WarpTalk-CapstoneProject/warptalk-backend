using System;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

/// <param name="Reason">
/// Why the reviewer decided this. REQUIRED when <paramref name="Approve"/> is false.
///
/// WT-633: rejection already worked — the status went to `rejected`, an audit row was written, and
/// the uploader kept sight of the document. What never existed anywhere was the reviewer's reason.
/// The audit call passed no metadata at all, so "Admin từ chối và nhập lý do" had nowhere to be
/// typed and nowhere to be stored, and the uploader's only recourse was to delete and start again.
///
/// Trailing and optional on the record so the approve path keeps its one-argument construction;
/// the service rejects a blank reason on the reject branch.
/// </param>
public record ApproveDocumentRequest(bool Approve, string? Reason = null);
