using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using WarpTalk.WorkspaceService.Domain.Constants;

namespace WarpTalk.WorkspaceService.Application.Helpers;

/// <summary>
/// What a file's own first bytes say it is, and the hash that says whether we already have it.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS
///     Upload validation asked exactly one question about the payload: does the FILE NAME end in
///     an extension we accept. Renaming <c>payload.exe</c> to <c>payload.pdf</c> answered it, and
///     the bytes were then encrypted, stored, handed to the text extractor and — for the four
///     AI-readable extensions — chunked into the vector store.
///
///     Both checks here run over the same in-memory copy of the upload, so the file is read once.
///     At the 10MB request cap that copy is bounded, and the storage layer already materialises
///     the whole payload to encrypt it.
/// </remarks>
public static class DocumentContentHelper
{
    private static readonly byte[] Pdf = "%PDF"u8.ToArray();
    private static readonly byte[] ZipLocalFile = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ZipEmpty = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] ZipSpanned = [0x50, 0x4B, 0x07, 0x08];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Gif87 = "GIF87a"u8.ToArray();
    private static readonly byte[] Gif89 = "GIF89a"u8.ToArray();
    private static readonly byte[] Bmp = "BM"u8.ToArray();
    private static readonly byte[] Riff = "RIFF"u8.ToArray();
    private static readonly byte[] Webp = "WEBP"u8.ToArray();

    /// <summary>
    /// Executable and archive headers that must never be stored whatever the extension claims.
    /// </summary>
    /// <remarks>
    /// Only consulted for <c>.md</c>, the one accepted extension with no signature of its own.
    /// Everything else is decided positively — it either starts with its format's magic bytes or
    /// it is refused — which is a stronger test than any blocklist.
    /// </remarks>
    private static readonly byte[][] ExecutableSignatures =
    [
        [0x4D, 0x5A],                    // DOS/PE — .exe, .dll
        [0x7F, 0x45, 0x4C, 0x46],        // ELF
        [0xCF, 0xFA, 0xED, 0xFE],        // Mach-O 64-bit little-endian
        [0xCE, 0xFA, 0xED, 0xFE],        // Mach-O 32-bit little-endian
        [0xCA, 0xFE, 0xBA, 0xBE],        // Mach-O fat binary / Java class
        [0x23, 0x21],                    // #! shebang
        [0x50, 0x4B, 0x03, 0x04],        // ZIP — .md is not an archive
        [0x52, 0x61, 0x72, 0x21],        // RAR
        [0x1F, 0x8B],                    // gzip
        [0x25, 0x50, 0x44, 0x46]         // PDF wearing a .md name
    ];

    /// <summary>
    /// Does this payload's own header match the format its extension claims?
    /// </summary>
    /// <param name="content">The upload, or at least its first <see cref="SignaturePrefixLength"/> bytes.</param>
    /// <param name="fileExtension">The normalized, already-supported extension.</param>
    /// <returns>
    /// False when the bytes contradict the extension. An extension this method does not know is
    /// NOT silently accepted — it returns false, so adding a format to
    /// <see cref="WorkspaceDocumentConstants.SupportedUploadExtensions"/> without adding its
    /// signature here fails closed rather than reopening the hole.
    /// </returns>
    public static bool MatchesExtensionSignature(ReadOnlySpan<byte> content, string? fileExtension)
    {
        var extension = WorkspaceDocumentHelper.NormalizeExtension(fileExtension);

        return extension switch
        {
            ".pdf" => StartsWith(content, Pdf),
            // DOCX and XLSX are ZIP containers. The container is all we can assert cheaply; the
            // extractor opens it properly and fails loudly on a ZIP that is not an Office package.
            ".docx" or ".xlsx" => StartsWith(content, ZipLocalFile)
                                  || StartsWith(content, ZipEmpty)
                                  || StartsWith(content, ZipSpanned),
            ".png" => StartsWith(content, Png),
            ".jpg" or ".jpeg" => StartsWith(content, Jpeg),
            ".gif" => StartsWith(content, Gif87) || StartsWith(content, Gif89),
            ".bmp" => StartsWith(content, Bmp),
            // RIFF....WEBP — four bytes of container, four of length, then the form type.
            ".webp" => StartsWith(content, Riff)
                       && content.Length >= 12
                       && content.Slice(8, 4).SequenceEqual(Webp),
            // Markdown has no header. It is text, so the test is that it is not something else:
            // no known binary signature, and no NUL byte in the prefix.
            ".md" => IsPlausibleText(content),
            _ => false
        };
    }

    /// <summary>
    /// A human-readable name for the format an extension promises, for the 400 body.
    /// </summary>
    public static string DescribeExpectedFormat(string? fileExtension)
    {
        var extension = WorkspaceDocumentHelper.NormalizeExtension(fileExtension);
        return extension switch
        {
            ".pdf" => "a PDF (%PDF)",
            ".docx" => "a Word document (ZIP/PK)",
            ".xlsx" => "an Excel workbook (ZIP/PK)",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => $"a {extension.TrimStart('.').ToUpperInvariant()} image",
            ".md" => "a text file",
            _ => "a supported document"
        };
    }

    /// <summary>
    /// Lowercase hex SHA-256 of the payload — the identity WT-666's duplicate check compares on.
    /// </summary>
    /// <remarks>
    /// Over the PLAINTEXT, deliberately. The stored blob is AES-CBC with a per-save random IV, so
    /// two identical uploads produce two different ciphertexts and hashing the encrypted form
    /// would never match anything.
    /// </remarks>
    public static string ComputeSha256(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    private static bool StartsWith(ReadOnlySpan<byte> content, ReadOnlySpan<byte> signature)
    {
        return content.Length >= signature.Length
            && content[..signature.Length].SequenceEqual(signature);
    }

    private static bool IsPlausibleText(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
        {
            return true;
        }

        foreach (var signature in ExecutableSignatures)
        {
            if (StartsWith(content, signature))
            {
                return false;
            }
        }

        return !content.Contains((byte)0x00);
    }
}
