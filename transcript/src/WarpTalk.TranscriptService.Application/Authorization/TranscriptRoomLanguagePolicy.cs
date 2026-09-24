using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.Services;
using GetTranslationRoomRequest = WarpTalk.Shared.Protos.GetTranslationRoomRequest;
using TranslationRoomServiceClient = WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient;

namespace WarpTalk.TranscriptService.Application.Authorization;

/// <summary>
/// WT-704: what the transcript service needs to know about a room before it GENERATES new language
/// content for it — a translation backfill or a machine-translated correction.
/// </summary>
/// <param name="EffectiveHostId">
/// The host after any transfer, falling back to the booker when an older TranslationRoomService
/// leaves <c>effective_host_id</c> empty. <c>null</c> only when neither id parses.
/// </param>
/// <param name="AllowedLanguages">
/// Bare, lower-case ISO-639 codes (normalized with
/// <see cref="TranscriptTranslationBackfillService.NormalizeLanguage"/>), distinct. Empty means
/// nothing may be generated.
/// </param>
public sealed record TranscriptRoomLanguageSnapshot(Guid? EffectiveHostId, IReadOnlyList<string> AllowedLanguages);

/// <summary>
/// WT-704: the single place the transcript service asks "which languages may new content for this
/// room be generated in".
///
/// Languages narrow as they go down: the workspace whitelist (L1) bounds what a meeting may declare
/// (L2), and a finished meeting's outputs are bounded by L2 ∩ L1. TranslationRoomService owns that
/// rule (<c>IRoomArtifactLanguagePolicy</c>); this port reads its answer off
/// <c>GetTranslationRoomResponse.generatable_artifact_languages</c> instead of recomputing it, so
/// the transcript and the summary/minutes endpoints cannot drift apart.
/// </summary>
/// <remarks>
/// Only GENERATION is gated. Translations that already exist stay readable whatever this says: a
/// policy tightened after the fact must not make an existing record unreadable.
/// </remarks>
public interface ITranscriptRoomLanguagePolicy
{
    /// <summary>
    /// The room's effective host and its generatable languages.
    ///
    /// When TranslationRoomService could not resolve the list (older server, workspace or catalog
    /// lookup failed) the answer falls back to the room's own declared set — source plus targets.
    /// That set was validated against the whitelist when the room was created or edited, so it
    /// fails closed to L2 rather than open to "any language".
    /// </summary>
    /// <returns>
    /// Failure <c>NOT_FOUND</c> when the room does not exist; <c>INTERNAL_ERROR</c> for any other
    /// failure to reach it.
    /// </returns>
    Task<Result<TranscriptRoomLanguageSnapshot>> GetAsync(Guid translationRoomId, CancellationToken ct = default);
}

/// <summary>WT-704: machine reasons the generation gates return, so clients can branch on them.</summary>
public static class TranscriptLanguageErrors
{
    /// <summary>The requested language is outside the room's generatable set (L2 ∩ L1).</summary>
    public const string LanguageNotAllowed = "LANGUAGE_NOT_ALLOWED";

    /// <summary>The transcript is finalized or archived and no longer accepts new content.</summary>
    public const string TranscriptLocked = "TRANSCRIPT_LOCKED";
}

public static partial class TranscriptRoomLanguageSnapshotExtensions
{
    /// <summary>
    /// True when <paramref name="normalizedCode"/> is a bare ISO-639 code (two or three letters) in
    /// the snapshot's allowed set. Free text such as "klingon" or a prompt fragment is refused
    /// before it can reach a model, even if something upstream let it into the list.
    /// </summary>
    public static bool IsAllowed(this TranscriptRoomLanguageSnapshot snapshot, string? normalizedCode)
    {
        if (string.IsNullOrEmpty(normalizedCode) || !IsoCode().IsMatch(normalizedCode))
            return false;

        return snapshot.AllowedLanguages.Contains(normalizedCode, StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^[a-z]{2,3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsoCode();
}

/// <inheritdoc cref="ITranscriptRoomLanguagePolicy"/>
public sealed class TranscriptRoomLanguagePolicy : ITranscriptRoomLanguagePolicy
{
    private readonly TranslationRoomServiceClient _roomClient;
    private readonly ILogger<TranscriptRoomLanguagePolicy> _logger;

    public TranscriptRoomLanguagePolicy(
        TranslationRoomServiceClient roomClient,
        ILogger<TranscriptRoomLanguagePolicy> logger)
    {
        _roomClient = roomClient;
        _logger = logger;
    }

    public async Task<Result<TranscriptRoomLanguageSnapshot>> GetAsync(
        Guid translationRoomId,
        CancellationToken ct = default)
    {
        try
        {
            // The only caller that opts in: the list costs TranslationRoomService a workspace RPC
            // and a catalog read, and every other reader of this RPC is on a hot path.
            var room = await _roomClient.GetTranslationRoomByIdAsync(
                new GetTranslationRoomRequest
                {
                    Id = translationRoomId.ToString(),
                    IncludeArtifactLanguages = true
                },
                cancellationToken: ct);

            // `resolved` is the only way to tell "the answer is empty" from "there is no answer" —
            // proto3 cannot. Unresolved falls back to the room's own declared set (L2), never
            // wider: an authorization input that is missing must not widen what may be generated.
            IEnumerable<string> languages = room.ArtifactLanguagesResolved
                ? room.GeneratableArtifactLanguages
                : new[] { room.SourceLanguage }.Concat(room.TargetLanguages);

            var allowed = languages
                .Select(TranscriptTranslationBackfillService.NormalizeLanguage)
                .Where(code => code.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // Same fallback as Finalize and TranscriptReadAccess: empty `effective_host_id` means an
            // older server, and the booker is who that server would have called the host.
            var effectiveHost = string.IsNullOrEmpty(room.EffectiveHostId) ? room.HostId : room.EffectiveHostId;
            Guid? hostId = Guid.TryParse(effectiveHost, out var parsedHostId) ? parsedHostId : null;

            return Result.Success(new TranscriptRoomLanguageSnapshot(hostId, allowed));
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return Result.Failure<TranscriptRoomLanguageSnapshot>("Translation room not found.", "NOT_FOUND");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error resolving generatable languages for room {RoomId}", translationRoomId);
            return Result.Failure<TranscriptRoomLanguageSnapshot>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }
}
