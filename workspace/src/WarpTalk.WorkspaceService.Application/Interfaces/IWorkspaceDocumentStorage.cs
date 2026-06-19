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
}
