using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;

namespace WarpTalk.TranscriptService.Application.Services;

public class TranscriptExportService : ITranscriptExportService
{
    private readonly IUnitOfWork _unitOfWork;

    public TranscriptExportService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<TranscriptExportDto> CreateExportAsync(Guid transcriptId, CreateTranscriptExportRequest request, Guid userId)
    {
        var transcript = await _unitOfWork.Transcripts.GetByIdAsync(transcriptId);
        if (transcript == null)
            throw new Exception("Transcript not found"); // Usually a custom NotFoundException

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

        // Note: For multi-tenant security, ensure export.UserId == userId if required.

        var segments = await _unitOfWork.TranscriptSegments.FindAsync(s => s.TranscriptId == transcriptId);
        
        var segmentIds = segments.Select(s => s.Id).ToList();
        var translations = await _unitOfWork.TranscriptTranslations.FindAsync(t => segmentIds.Contains(t.SegmentId));

        foreach (var segment in segments)
        {
            segment.TranscriptTranslations = translations.Where(t => t.SegmentId == segment.Id).ToList();
        }

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
            fileBytes = GenerateCsv(orderedSegments, includedLangs);
            contentType = "text/csv";
        }
        else // default to txt
        {
            fileBytes = GenerateTxt(orderedSegments, includedLangs);
            contentType = "text/plain";
            fileName = $"transcript_{transcriptId}.txt";
        }

        return (fileBytes, contentType, fileName);
    }

    private byte[] GenerateTxt(List<TranscriptSegment> segments, List<string> includedLangs)
    {
        var sb = new StringBuilder();
        
        foreach (var segment in segments)
        {
            // Add original text
            sb.AppendLine($"[{FormatTime(segment.StartTimeMs)} - {FormatTime(segment.EndTimeMs)}] {segment.SpeakerName} ({segment.OriginalLanguage}): {segment.OriginalText}");

            // Add translations
            if (includedLangs.Any())
            {
                foreach (var trans in segment.TranscriptTranslations)
                {
                    if (includedLangs.Contains(trans.TargetLanguage, StringComparer.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"  └─ [{trans.TargetLanguage}]: {trans.TranslatedText}");
                    }
                }
            }
            sb.AppendLine();
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private byte[] GenerateCsv(List<TranscriptSegment> segments, List<string> includedLangs)
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

            foreach (var lang in includedLangs)
            {
                var trans = segment.TranscriptTranslations.FirstOrDefault(t => t.TargetLanguage.Equals(lang, StringComparison.OrdinalIgnoreCase));
                row.Add(trans != null ? EscapeCsv(trans.TranslatedText) : string.Empty);
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
}
