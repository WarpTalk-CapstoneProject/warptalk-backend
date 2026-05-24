using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using WarpTalk.Shared;

namespace WarpTalk.Shared.Filters;

public class WarpTalkResultFilter : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult objectResult)
        {
            if (objectResult.Value is Result result)
            {
                if (!result.IsSuccess)
                {
                    context.Result = MapToErrorResult(result);
                }
                else
                {
                    // Extract Value property for generic Result<T>
                    var valueProp = result.GetType().GetProperty("Value");
                    if (valueProp != null)
                    {
                        var data = valueProp.GetValue(result);
                        context.Result = new OkObjectResult(data);
                    }
                    else
                    {
                        context.Result = new NoContentResult();
                    }
                }
            }
        }

        await next();
    }

    private IActionResult MapToErrorResult(Result result)
    {
        var errorResponse = new ApiErrorResponse(result.Error, result.ErrorCode);

        return result.ErrorCode switch
        {
            ErrorCodes.Forbidden => new ObjectResult(errorResponse) { StatusCode = StatusCodes.Status403Forbidden },
            ErrorCodes.NotFound or ErrorCodes.UserNotFound => new NotFoundObjectResult(errorResponse),
            ErrorCodes.ValidationError => new BadRequestObjectResult(errorResponse),
            ErrorCodes.RateLimitExceeded or ErrorCodes.CooldownActive => new ObjectResult(errorResponse) { StatusCode = StatusCodes.Status429TooManyRequests },
            ErrorCodes.Unauthorized or ErrorCodes.InvalidCredentials or ErrorCodes.InvalidToken => new UnauthorizedObjectResult(errorResponse),
            ErrorCodes.AccountInactive or ErrorCodes.AccountLocked or ErrorCodes.AccountPending => new BadRequestObjectResult(errorResponse),
            ErrorCodes.InvalidState => new ConflictObjectResult(errorResponse),
            _ => new BadRequestObjectResult(errorResponse)
        };
    }
}
