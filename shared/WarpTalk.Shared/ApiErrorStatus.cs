using Microsoft.AspNetCore.Http;

namespace WarpTalk.Shared;

/// <summary>
/// WT-596: one place that decides which HTTP status an <see cref="ErrorCodes"/> value deserves.
///
/// Controllers used to answer this question inline, and every one of them answered it the same
/// wrong way: map the one or two codes the endpoint cares about, then <c>BadRequest</c> for
/// everything else. That default is what shipped <c>INTERNAL_SERVER_ERROR</c> as 400 — a status
/// that says the caller made a mistake, for a failure the caller had no part in.
///
/// The rule is what the status CLAIMS, not how severe the failure feels:
///   4xx — we looked at the request and it is wrong or not permitted.
///   5xx — we could not answer. Worth retrying; never the caller's fault.
///
/// An unrecognised code falls to <see cref="StatusCodes.Status400BadRequest"/>, because every
/// service-specific code (BILLING_*, MEETING_*) that reaches here is a rejection of the request.
/// A new failure-to-answer code must be added to the 5xx list below, not left to the default.
/// </summary>
public static class ApiErrorStatus
{
    /// <summary>The status code <paramref name="errorCode"/> should be reported with.</summary>
    public static int For(string? errorCode) => errorCode switch
    {
        // Could not answer.
        ErrorCodes.ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
        ErrorCodes.InternalServerError => StatusCodes.Status500InternalServerError,

        // Answered: no.
        ErrorCodes.Unauthorized or ErrorCodes.InvalidCredentials => StatusCodes.Status401Unauthorized,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.NotFound or ErrorCodes.UserNotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Conflict or ErrorCodes.EmailExists => StatusCodes.Status409Conflict,
        ErrorCodes.RateLimitExceeded or ErrorCodes.CooldownActive => StatusCodes.Status429TooManyRequests,

        _ => StatusCodes.Status400BadRequest,
    };

    /// <summary>True when <paramref name="errorCode"/> means we failed to answer rather than refused.</summary>
    public static bool IsServiceFault(string? errorCode)
        => errorCode is ErrorCodes.ServiceUnavailable or ErrorCodes.InternalServerError;
}
