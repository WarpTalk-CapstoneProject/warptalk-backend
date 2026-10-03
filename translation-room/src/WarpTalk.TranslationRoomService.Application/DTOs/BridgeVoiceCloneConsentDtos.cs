using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// PUT /api/v1/translation-rooms/{id}/bridge/voice-clone-consents —
/// <c>{ "displayName": "Trần An", "consented": true }</c>. The host ticks (true) or unticks
/// (false) one Meet-side person by the name Meet shows for them.
/// </summary>
public record SetBridgeVoiceCloneConsentRequest(string? DisplayName, bool Consented);

/// <summary>
/// The request, echoed: the name exactly as submitted and the state it is now in. The name is
/// only ever in the request and this answer — it is not stored and not logged.
/// </summary>
public record BridgeVoiceCloneConsentDto(string DisplayName, bool Consented);

/// <summary>
/// POST /api/v1/translation-rooms/{id}/bridge/voice-clone-consents/status —
/// <c>{ "displayNames": ["Trần An", "Tú Huỳnh"] }</c>. A POST with a body on purpose: names
/// must not travel in a URL, where proxies and access logs would keep them.
/// </summary>
public record BridgeVoiceCloneConsentStatusRequest(IReadOnlyList<string?>? DisplayNames);

/// <summary>
/// The subset of the submitted names that have a consent in force, each spelled exactly as it
/// was submitted and in the order it was submitted.
/// </summary>
public record BridgeVoiceCloneConsentStatusDto(IReadOnlyList<string> Consented);
