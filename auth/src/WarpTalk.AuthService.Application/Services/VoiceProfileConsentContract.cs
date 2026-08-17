using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using WarpTalk.AuthService.Application.DTOs;

namespace WarpTalk.AuthService.Application.Services;

/// <summary>
/// Domain contract static helper for voice profile consent validation, Magic Bytes audio inspection,
/// and binary audio cryptographic checksum binding (WT-495).
/// </summary>
public static class VoiceProfileConsentContract
{
    public const string VersionPrefix = "voice-v1";
    public const string UploadConsentType = "VOICE_PROFILE_UPLOAD";
    public const string GrantedStatus = "GRANTED";

    public const string CanonicalContractText =
        "1. This is my own voice. " +
        "2. I allow WarpTalk to use this voice profile for AI speech translation. " +
        "3. I understand generated speech may sound like me in supported languages. " +
        "4. I will not use this voice profile to impersonate, deceive, or mislead others. " +
        "5. I understand I can delete or revoke this voice profile later.";

    public static bool IsValidConsentRequest(CreateVoiceProfileRequest? request)
    {
        if (request is null) return false;
        return request.OwnVoiceConfirmed &&
               request.AiUseConfirmed &&
               request.SyntheticVoiceAcknowledged &&
               request.NoImpersonationConfirmed &&
               request.RetentionAcknowledged;
    }

    /// <summary>
    /// Inspects stream header bytes (Magic Bytes) to verify the file is genuine audio
    /// (RIFF/WAV, OggS, ID3/MP3, EBML/WebM, ftyp/M4A) and not a disguised script/executable.
    /// </summary>
    public static bool ValidateAudioMagicBytes(byte[] headerBytes)
    {
        if (headerBytes is null || headerBytes.Length < 4) return false;

        // WAV: "RIFF" (0x52, 0x49, 0x46, 0x46)
        if (headerBytes[0] == 0x52 && headerBytes[1] == 0x49 && headerBytes[2] == 0x46 && headerBytes[3] == 0x46)
            return true;

        // OGG: "OggS" (0x4F, 0x67, 0x67, 0x53)
        if (headerBytes[0] == 0x4F && headerBytes[1] == 0x67 && headerBytes[2] == 0x67 && headerBytes[3] == 0x53)
            return true;

        // MP3 with ID3 header: "ID3" (0x49, 0x44, 0x33)
        if (headerBytes[0] == 0x49 && headerBytes[1] == 0x44 && headerBytes[2] == 0x33)
            return true;

        // MP3 frame sync header: 0xFF 0xFB, 0xFF 0xFA, 0xFF 0xF3, 0xFF 0xF2
        if (headerBytes[0] == 0xFF && (headerBytes[1] & 0xE0) == 0xE0)
            return true;

        // WebM / EBML: 0x1A 0x45 0xDF 0xA3
        if (headerBytes[0] == 0x1A && headerBytes[1] == 0x45 && headerBytes[2] == 0xDF && headerBytes[3] == 0xA3)
            return true;

        // M4A / MP4: "ftyp" box at offset 4 (bytes 4-7: 0x66, 0x74, 0x79, 0x70)
        if (headerBytes.Length >= 8 &&
            headerBytes[4] == 0x66 && headerBytes[5] == 0x74 && headerBytes[6] == 0x79 && headerBytes[7] == 0x70)
            return true;

        return false;
    }

    /// <summary>
    /// Computes cryptographic binary-consent binding hash in format: voice-v1:<audio_hash>:<contract_hash>
    /// Total length ~28 chars, easily fitting in VARCHAR(50).
    /// </summary>
    public static string ComputeVersionWithAudioHash(byte[]? audioBytes)
    {
        using var sha256 = SHA256.Create();

        // 1. Hash contract text (8 chars)
        var contractBytes = Encoding.UTF8.GetBytes(CanonicalContractText);
        var contractHash = Convert.ToHexString(sha256.ComputeHash(contractBytes))[..8].ToLowerInvariant();

        // 2. Hash audio binary stream if available (8 chars)
        var audioHash = "00000000";
        if (audioBytes is { Length: > 0 })
        {
            audioHash = Convert.ToHexString(sha256.ComputeHash(audioBytes))[..8].ToLowerInvariant();
        }

        return $"{VersionPrefix}:{audioHash}:{contractHash}";
    }
}
