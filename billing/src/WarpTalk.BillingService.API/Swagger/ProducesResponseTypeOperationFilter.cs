using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace WarpTalk.BillingService.API.Swagger;

/// <summary>
/// Custom operation filter to include ProducesResponseType attributes in Swagger documentation.
/// </summary>
public class ProducesResponseTypeOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.ActionDescriptor is not ControllerActionDescriptor controllerActionDescriptor)
            return;

        var methodInfo = controllerActionDescriptor.MethodInfo;
        var producesResponseTypeAttributes = methodInfo
            .GetCustomAttributes(typeof(ProducesResponseTypeAttribute), false)
            .Cast<ProducesResponseTypeAttribute>()
            .ToList();

        if (!producesResponseTypeAttributes.Any())
            return;

        // Clear default responses if we have explicit ones defined
        operation.Responses.Clear();

        foreach (var attr in producesResponseTypeAttributes)
        {
            var statusCode = attr.StatusCode.ToString();
            var description = GetDescriptionForStatusCode(statusCode);

            if (!operation.Responses.ContainsKey(statusCode))
            {
                var response = new OpenApiResponse
                {
                    Description = description,
                    Content = new Dictionary<string, OpenApiMediaType>()
                };

                // Add content type if type is specified
                if (attr.Type != null)
                {
                    response.Content["application/json"] = new OpenApiMediaType
                    {
                        Schema = context.SchemaGenerator.GenerateSchema(attr.Type, context.SchemaRepository)
                    };
                }

                operation.Responses.Add(statusCode, response);
            }
        }
    }

    private static string GetDescriptionForStatusCode(string statusCode)
    {
        return statusCode switch
        {
            "200" => "OK - Request successful",
            "201" => "Created - Resource created successfully",
            "204" => "No Content - Request successful, no content to return",
            "400" => "Bad Request - Invalid request parameters or validation failed",
            "401" => "Unauthorized - Missing or invalid authentication",
            "402" => "Payment Required - Insufficient credits",
            "403" => "Forbidden - Access denied",
            "404" => "Not Found - Resource not found",
            "409" => "Conflict - Resource already exists or concurrent modification",
            "500" => "Internal Server Error - Server error occurred",
            _ => $"HTTP {statusCode}"
        };
    }
}
