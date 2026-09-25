using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.NotificationService.API.Controllers;

/// <summary>
/// What every admin CMS controller shares: the actor comes from the authenticated claims only
/// (never the body — a client-supplied actor would make the audit trail forgeable), and a failed
/// result maps to its status in one place.
/// </summary>
public abstract class CmsControllerBase : ControllerBase
{
    protected async Task<IActionResult> AsActor<T>(Func<AdminActorContext, Task<Result<T>>> action, Func<T, IActionResult>? onSuccess = null)
    {
        if (!AdminActorContext.TryResolve(User, HttpContext, out var actor)) return Unauthorized();
        var result = await action(actor);
        if (!result.IsSuccess) return ControllerResults.Failure(this, result.Error, result.ErrorCode);
        return onSuccess is null ? Ok(result.Value) : onSuccess(result.Value!);
    }

    protected async Task<IActionResult> AsActor(Func<AdminActorContext, Task<Result>> action)
    {
        if (!AdminActorContext.TryResolve(User, HttpContext, out var actor)) return Unauthorized();
        var result = await action(actor);
        return result.IsSuccess ? NoContent() : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    protected IActionResult From<T>(Result<T> result) =>
        result.IsSuccess ? Ok(result.Value) : ControllerResults.Failure(this, result.Error, result.ErrorCode);
}
