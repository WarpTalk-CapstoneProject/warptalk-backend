using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.Shared;

namespace WarpTalk.TranslationRoomService.API.Extensions;

public static class ServiceCollectionExtensions
{
    //standardized for validation error before controller
    public static IServiceCollection AddCustomApiBehavior(this IServiceCollection services)
    {
        return services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var errors = context.ModelState
                    .Where(e => e.Value != null && e.Value.Errors.Count > 0)
                    .SelectMany(x => x.Value!.Errors)
                    .Select(x => x.ErrorMessage)
                    .ToList();
                
                return new BadRequestObjectResult(new ApiErrorResponse(string.Join(" ", errors), ErrorCodes.ValidationError));
            };
        });
    }
}
