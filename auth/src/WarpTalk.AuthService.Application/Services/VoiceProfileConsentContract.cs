using System;
using System.Security.Cryptography;
using System.Text;
using WarpTalk.AuthService.Domain.Enums;

namespace WarpTalk.AuthService.Application.Services;

internal static class VoiceProfileConsentContract
{
    public const string UploadConsentType = "voice_profile_upload";
    public const ConsentStatus GrantedStatus = ConsentStatus.GRANTED;
    public const string Version = "voice-profile-upload-v1";

    public const string Snapshot =
        "WarpTalk voice profile upload consent v1\n"
        + "1. This is my own voice.\n"
        + "2. I allow WarpTalk to use this voice profile for AI speech translation.\n"
        + "3. I understand generated speech may sound like me in supported languages.\n"
        + "4. I will not use this voice profile to impersonate, deceive, or mislead others.\n"
        + "5. I understand I can delete this voice profile later.";

    public static string SnapshotHash()
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Snapshot))).ToLowerInvariant();

    public static string PublicStatus(ConsentStatus status)
        => status.ToString().ToLowerInvariant();
}
