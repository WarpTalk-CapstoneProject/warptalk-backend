namespace WarpTalk.Shared;

/// <summary>
/// Typed error response DTO for REST APIs.
/// Replaces anonymous <c>new { error, code }</c> objects in controllers.
/// </summary>
public record ApiErrorResponse(string? Error, string? Code);
