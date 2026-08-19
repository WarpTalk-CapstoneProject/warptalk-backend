namespace WarpTalk.MeetingService.Application.DTOs;

/// <summary>
/// WT-525. A LiveKit token for the stand-in participant of an EXTERNAL_BRIDGE room — the seat
/// that represents everyone on the far side of a Google Meet call.
///
/// This is the only token this service mints for an identity that is NOT the caller. Every other
/// path derives the identity from the authenticated user precisely so a caller cannot speak as
/// somebody else, and that property is what makes the pipeline's speaker attribution trustworthy:
/// stt_worker reads speaker_id straight off participant_identity.
///
/// So the endpoint behind this is gated twice, and both gates are load-bearing rather than
/// defensive: the caller must be the room's HOST, and the room must actually BE an external
/// bridge. Either gate alone would be a hole — a host of an ordinary room could mint a ghost
/// participant into their own meeting, and a non-host could mint one into someone else's.
/// </summary>
public class BridgeTokenResponse
{
    /// <summary>Publish-only token. The stand-in speaks for the far side; it never listens.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>The LiveKit room, same one the host is already connected to.</summary>
    public string ProviderRoomName { get; set; } = string.Empty;

    /// <summary>
    /// Always TranslationRoomConstants.ExternalBridgeParticipantUserId. Returned rather than left
    /// for the client to hardcode: the pipeline routes on this exact string, and a client that
    /// spelled it itself would be a second definition of the identity free to drift from the one
    /// the room was seeded with.
    /// </summary>
    public string ParticipantIdentity { get; set; } = string.Empty;
}
