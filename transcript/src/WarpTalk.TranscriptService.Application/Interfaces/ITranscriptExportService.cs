using System;
using System.Threading.Tasks;
using WarpTalk.TranscriptService.Application.DTOs;

namespace WarpTalk.TranscriptService.Application.Interfaces;

public interface ITranscriptExportService
{
    Task<TranscriptExportDto> CreateExportAsync(Guid transcriptId, CreateTranscriptExportRequest request, Guid userId, string? userEmail = null);
    Task<(byte[] FileBytes, string ContentType, string FileName)> DownloadExportAsync(Guid transcriptId, Guid exportId, Guid userId, string? userEmail = null);
}
