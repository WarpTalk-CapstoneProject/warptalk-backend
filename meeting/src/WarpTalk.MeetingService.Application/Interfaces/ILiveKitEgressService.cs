using System.Text.Json;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

/// <summary>
/// WT-06: starts/stops a LiveKit RoomComposite Egress (recording) for a room. There is no
/// LiveKit server SDK dependency in this codebase — LiveKitTokenService hand-rolls its JWTs
/// with plain JwtSecurityTokenHandler rather than a LiveKit NuGet package — so this follows
/// the same convention: a thin, hand-rolled HTTP client against LiveKit's Twirp-based Egress
/// API, instead of introducing a brand-new external dependency.
/// </summary>
public interface ILiveKitEgressService
{
    Task<Result<string>> StartRoomCompositeEgressAsync(string roomName, CancellationToken ct = default);
    Task<Result<bool>> StopEgressAsync(string egressId, CancellationToken ct = default);

    /// <summary>
    /// LiveKit's own record of one egress, or a success carrying <c>null</c> when LiveKit has
    /// never heard of that id. Not knowing an id is a normal answer, not a failure — it is what
    /// LiveKit returns once an old egress has aged out of its history.
    ///
    /// WHY THIS EXISTS (WT-371 #8). Recording had exactly ONE completion path: LiveKit's
    /// <c>egress_ended</c> webhook. In production that webhook was never configured, so every
    /// recording started, ran, uploaded to S3 — and was never heard from again. Five rooms sat
    /// holding an ActiveEgressId for five days, the UI said "recording" forever, and not one
    /// artifact row was ever written. Nothing anywhere noticed.
    ///
    /// A completion path that depends on a single externally-configured callback, with no way to
    /// ask "did it finish?", cannot detect its own failure. This is the question that closes
    /// that gap; <c>IEgressReconciliation</c> is what asks it on a timer.
    ///
    /// Returns the raw <c>EgressInfo</c> element, DELIBERATELY the same shape the webhook body
    /// carries, so both paths can hand it to the same completion code and cannot drift apart.
    /// The element is detached from its document, so it stays valid after this returns.
    /// </summary>
    Task<Result<JsonElement?>> GetEgressAsync(string egressId, CancellationToken ct = default);
}
