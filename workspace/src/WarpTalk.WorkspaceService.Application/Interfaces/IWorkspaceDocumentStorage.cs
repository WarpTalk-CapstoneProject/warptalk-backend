using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>
/// Interface for application-level encrypted document storage (AES-256-CBC + HMAC-SHA512).
/// Handles encrypting on write and decrypting/verifying integrity on read.
/// </summary>
public interface IWorkspaceDocumentStorage
{
    /// <summary>
    /// The name of the underlying storage provider (e.g., Local, MinIO, S3).
    /// </summary>
    string StorageProviderName { get; }

    /// <summary>
    /// Reads the decrypted content of a document as a UTF-8 string (useful for plain text/md files).
    /// </summary>
    Task<string> ReadDocumentContentAsync(WorkspaceDocument document, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the decrypted stream of a document (useful for binary files like PDF/Word parsing).
    /// </summary>
    Task<Stream> GetDecryptedStreamAsync(WorkspaceDocument document, CancellationToken ct = default);

    /// <summary>
    /// Encrypts and saves the content stream to physical storage.
    /// </summary>
    Task SaveDocumentContentAsync(WorkspaceDocument document, Stream contentStream, CancellationToken ct = default);

    /// <summary>
    /// Encrypts and saves the extracted text of a document separately.
    /// </summary>
    Task SaveExtractedTextAsync(WorkspaceDocument document, string text, CancellationToken ct = default);

    /// <summary>
    /// Decrypts and reads the stored extracted text of a document.
    /// </summary>
    Task<string> GetExtractedTextAsync(WorkspaceDocument document, CancellationToken ct = default);

    /// <summary>
    /// Encrypts and saves the PII-masked copy of the document's current file. It is a file of the
    /// SAME FORMAT as the original (a masked .docx for a .docx), stored next to it as
    /// <c>{StorageKey}_masked{extension}</c>. Keyed by StorageKey, so a new revision never
    /// inherits an older file's masked copy.
    /// </summary>
    Task SaveMaskedFileAsync(WorkspaceDocument document, Stream contentStream, CancellationToken ct = default);

    /// <summary>
    /// Decrypts the masked copy, or returns null when none exists for the current file. A copy
    /// that exists but fails its integrity check throws: callers must treat that as "nothing to
    /// serve", never as a reason to fall back to the original.
    /// </summary>
    Task<Stream?> GetMaskedFileStreamAsync(WorkspaceDocument document, CancellationToken ct = default);

    /// <summary>
    /// Whether a masked copy exists for the current file, without decrypting it.
    /// </summary>
    Task<bool> MaskedFileExistsAsync(WorkspaceDocument document, CancellationToken ct = default);

    /// <summary>
    /// Removes the masked copy of the current file, if any. Throws when the store refuses, so a
    /// caller that must not leave a stale copy behind can tell.
    /// </summary>
    Task DeleteMaskedFileAsync(WorkspaceDocument document, CancellationToken ct = default);

    /// <summary>
    /// Deletes the physical storage blob for a document, if it exists. Best-effort compensating
    /// action for callers that wrote the blob before a subsequent step (e.g. the DB insert) failed.
    /// </summary>
    Task DeleteDocumentContentAsync(WorkspaceDocument document, CancellationToken ct = default);
}
