using System.Net;
using System.Text.Json;
using WarpTalk.BillingService.Domain.Exceptions;

namespace WarpTalk.BillingService.API.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unhandled exception. TraceId={TraceId}, Path={Path}",
                context.TraceIdentifier,
                context.Request.Path);

            await HandleExceptionAsync(context, ex);
        }
    }

    private static async Task HandleExceptionAsync(
        HttpContext context,
        Exception exception)
    {
        context.Response.ContentType = "application/problem+json";

        var (statusCode, message) = MapException(exception);

        context.Response.StatusCode = statusCode;

        var response = new
        {
            type = "https://httpstatuses.com/" + statusCode,
            title = message,
            status = statusCode,
            traceId = context.TraceIdentifier
        };

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(response));
    }

    private static (int statusCode, string message) MapException(Exception ex)
    {
        return ex switch
        {
            BillingDomainException e =>
                ((int)HttpStatusCode.BadRequest, e.Message),

            KeyNotFoundException =>
                ((int)HttpStatusCode.NotFound,
                    "Not found."),

            InvalidOperationException e when
                e.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) =>
                ((int)HttpStatusCode.Conflict,
                    "Duplicate operation."),

            _ =>
                ((int)HttpStatusCode.InternalServerError,
                    "Unexpected error.")
        };
    }
}