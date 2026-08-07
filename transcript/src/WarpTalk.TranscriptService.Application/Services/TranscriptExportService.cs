using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WarpTalk.TranscriptService.Application.Authorization;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;

namespace WarpTalk.TranscriptService.Application.Services;

public class TranscriptExportService : ITranscriptExportService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranscriptReadAccess _readAccess;

    public TranscriptExportService(IUnitOfWork unitOfWork, ITranscriptReadAccess readAccess)
    {
        _unitOfWork = unitOfWork;
        _readAccess = readAccess;
    }

    public async Task<TranscriptExportDto> CreateExportAsync(Guid transcriptId, CreateTranscriptExportRequest request, Guid userId)
    {
        var transcript = await _unitOfWork.Transcripts.GetByIdAsync(transcriptId);
        if (transcript == null)
            throw new Exception("Transcript not found"); // Usually a custom NotFoundException

        if (!await CanAccessTranscriptAsync(transcript, userId))
            throw new UnauthorizedAccessException("You do not have access to this transcript.");

        var exportId = Guid.NewGuid(); // Alternatively, rely on DB to generate UUID

        var includedLanguages = JsonSerializer.Serialize(request.IncludedLanguages ?? new List<string>());

        var export = new TranscriptExport
        {
            Id = exportId,
            TranscriptId = transcriptId,
            UserId = userId,
            Format = request.Format.ToLowerInvariant(),
            IncludedLanguages = includedLanguages,
            FileUrl = $"/api/v1/transcripts/{transcriptId}/exports/{exportId}/download",
            CreatedAt = DateTime.UtcNow
        };

        await _unitOfWork.TranscriptExports.AddAsync(export);
        await _unitOfWork.SaveChangesAsync();

        return new TranscriptExportDto(
            export.Id,
            export.TranscriptId,
            export.UserId,
            export.Format,
            export.FileUrl,
            request.IncludedLanguages ?? new List<string>(),
            export.CreatedAt
        );
    }

    public async Task<(byte[] FileBytes, string ContentType, string FileName)> DownloadExportAsync(Guid transcriptId, Guid exportId, Guid userId)
    {
        var export = await _unitOfWork.TranscriptExports.GetByIdAsync(exportId);
        if (export == null || export.TranscriptId != transcriptId)
            throw new Exception("Export not found");

        var transcript = await _unitOfWork.Transcripts.GetByIdAsync(transcriptId);
        if (transcript == null)
            throw new Exception("Transcript not found");

        if (!await CanAccessTranscriptAsync(transcript, userId))
            throw new UnauthorizedAccessException("You do not have access to this transcript export.");

        var segments = await _unitOfWork.TranscriptSegments.FindAsync(s => s.TranscriptId == transcriptId);

        var segmentIds = segments.Select(s => s.Id).ToList();

        // Only the CURRENT link per (segment, language) — a re-translated segment must export
        // the latest text, not a superseded one. Replaces the old TranscriptTranslations join.
        var currentLinks = (await _unitOfWork.SegmentTranslationLinks.FindAsync(
                l => segmentIds.Contains(l.SegmentId) && l.IsCurrent))
            .ToList();
        var contentIds = currentLinks.Select(l => l.TranslationContentId).Distinct().ToList();
        var contentById = (await _unitOfWork.TranslationContents.FindAsync(tc => contentIds.Contains(tc.Id)))
            .ToDictionary(tc => tc.Id);

        var translationsBySegment = currentLinks
            .Where(l => contentById.ContainsKey(l.TranslationContentId))
            .ToLookup(l => l.SegmentId, l => (Lang: l.TargetLanguage, Text: contentById[l.TranslationContentId].TranslatedText));

        var orderedSegments = segments.OrderBy(s => s.SequenceOrder).ToList();

        List<string> includedLangs = new();
        try
        {
            includedLangs = JsonSerializer.Deserialize<List<string>>(export.IncludedLanguages) ?? new List<string>();
        }
        catch { }

        byte[] fileBytes;
        string contentType;
        string fileName = $"transcript_{transcriptId}.{export.Format}";

        if (export.Format == "csv")
        {
            fileBytes = GenerateCsv(orderedSegments, includedLangs, translationsBySegment);
            contentType = "text/csv";
        }
        else // default to txt
        {
            fileBytes = GenerateTxt(orderedSegments, includedLangs, translationsBySegment);
            contentType = "text/plain";
            fileName = $"transcript_{transcriptId}.txt";
        }

        return (fileBytes, contentType, fileName);
    }

    private byte[] GenerateTxt(List<TranscriptSegment> segments, List<string> includedLangs, ILookup<Guid, (string Lang, string Text)> translationsBySegment)
    {
        var sb = new StringBuilder();

        foreach (var segment in segments)
        {
            // Add original text
            sb.AppendLine($"[{FormatTime(segment.StartTimeMs)} - {FormatTime(segment.EndTimeMs)}] {segment.SpeakerName} ({segment.OriginalLanguage}): {segment.OriginalText}");

            // Add translations
            if (includedLangs.Any())
            {
                foreach (var trans in translationsBySegment[segment.Id])
                {
                    if (includedLangs.Contains(trans.Lang, StringComparer.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"  └─ [{trans.Lang}]: {trans.Text}");
                    }
                }
            }
            sb.AppendLine();
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private byte[] GenerateCsv(List<TranscriptSegment> segments, List<string> includedLangs, ILookup<Guid, (string Lang, string Text)> translationsBySegment)
    {
        var sb = new StringBuilder();

        // Header
        var headers = new List<string> { "StartTime", "EndTime", "Speaker", "OriginalLanguage", "OriginalText" };
        headers.AddRange(includedLangs.Select(lang => $"Translated_{lang}"));
        sb.AppendLine(string.Join(",", headers));

        foreach (var segment in segments)
        {
            var row = new List<string>
            {
                FormatTime(segment.StartTimeMs),
                FormatTime(segment.EndTimeMs),
                EscapeCsv(segment.SpeakerName),
                EscapeCsv(segment.OriginalLanguage),
                EscapeCsv(segment.OriginalText)
            };

            var segmentTranslations = translationsBySegment[segment.Id];
            foreach (var lang in includedLangs)
            {
                var trans = segmentTranslations.FirstOrDefault(t => t.Lang.Equals(lang, StringComparison.OrdinalIgnoreCase));
                row.Add(trans.Text != null ? EscapeCsv(trans.Text) : string.Empty);
            }

            sb.AppendLine(string.Join(",", row));
        }

        // Output as UTF-8 with BOM for Excel compatibility
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var bom = Encoding.UTF8.GetPreamble();
        return bom.Concat(bytes).ToArray();
    }

    private string FormatTime(int ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.ToString(@"hh\:mm\:ss");
    }

    private string EscapeCsv(string field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        if (field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }

    private Task<bool> CanAccessTranscriptAsync(Transcript transcript, Guid userId)
        => _readAccess.CanReadRoomTranscriptAsync(transcript.TranslationRoomId, userId);
}
