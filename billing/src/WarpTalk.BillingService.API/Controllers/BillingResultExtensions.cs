using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.API.Controllers;

/// <summary>
/// The status a billing failure deserves, rather than 400 Bad Request for all of them.
///
/// WHY THIS EXISTS
///     Every read in this service answered failure with BadRequest. A workspace with no
///     subscription is the common one: nothing about the request is bad — the URL, the id and
///     the caller's role are all fine — the workspace simply has no plan yet. A client cannot
///     tell that apart from a malformed request, so the only thing it could honestly render was
///     an error where there is no error. That is how the owner dashboard came to show "Failed to
///     load chart data" as its permanent resting state on any workspace without a subscription.
///
///     The other conflation is the opposite mistake: a genuine server fault (a database that is
///     down, a mapper that threw) also answered 400. The client's retry policy treats 4xx as
///     never worth retrying — correctly — so a transient fault was permanent to the user.
///
/// SEPARATING THEM
///     404 — no subscription, no record. An account state the UI can name.
///     403 — the caller may not see it.
///     500 — this service broke. Worth retrying, and worth alerting on.
///     400 — anything genuinely wrong with the request, which stays the default because an
///           unclassified failure is more often a bad argument than a broken server.
///
/// AdminWorkspaceAnalyticsController keeps its own mapping: its default is 500, not 400, which is
/// a deliberately different stance for an internal admin surface and not something to change as a
/// side effect of fixing the dashboard.
/// </summary>
public static class BillingResultExtensions
{
    public static ActionResult<T> ToActionResult<T>(this ControllerBase controller, Result<T> result)
    {
        if (result.IsSuccess) return controller.Ok(result.Value);

        var error = new ApiErrorResponse(
            result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError,
            result.ErrorCode);

        return result.ErrorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound => controller.NotFound(error),
            ErrorCodes.BillingPlanNotFound => controller.NotFound(error),
            ErrorCodes.BillingWorkspaceNotFound => controller.NotFound(error),
            ErrorCodes.BillingTransactionNotFound => controller.NotFound(error),
            ErrorCodes.NotFound => controller.NotFound(error),
            ErrorCodes.Forbidden => controller.StatusCode(StatusCodes.Status403Forbidden, error),
            ErrorCodes.InternalServerError =>
                controller.StatusCode(StatusCodes.Status500InternalServerError, error),
            _ => controller.BadRequest(error),
        };
    }
}
