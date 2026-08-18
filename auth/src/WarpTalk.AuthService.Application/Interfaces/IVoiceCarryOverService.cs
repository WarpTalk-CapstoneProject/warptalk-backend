using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.AuthService.Application.Interfaces;

/// <summary>
/// Applies one carried-over clone announcement to this person's voice profiles (WT-B).
/// </summary>
public interface IVoiceCarryOverService
{
    /// <summary>
    /// Idempotent by design: the consumer acknowledges only after this commits, so a crash in
    /// between redelivers the same message and it must land on the same state.
    /// </summary>
    Task ApplyAsync(VoiceCarryOverMessage message, CancellationToken ct = default);
}
