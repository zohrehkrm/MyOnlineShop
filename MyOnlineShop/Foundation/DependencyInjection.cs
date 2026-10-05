using Microsoft.AspNetCore.Mvc;
using MyOnlineShop.BuildingBlocks.Abstractions;

namespace MyOnlineShop.Foundation;

public static class DependencyInjection
{
    public static IServiceCollection AddApiFoundation(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<IRequestContext, HttpRequestContext>();
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            ApiProblemDetails.Enrich(context.HttpContext, context.ProblemDetails));
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddControllers(options => options.Filters.Add<ApiProblemDetailsFilter>())
            .ConfigureApiBehaviorOptions(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                // Avoid model-binding exception text and attempted values in responses.
                var errors = context.ModelState.Where(entry => entry.Value?.Errors.Count > 0)
                    .ToDictionary(entry => entry.Key, _ => new[] { "The supplied value is invalid." });
                var problem = new ValidationProblemDetails(errors)
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "One or more validation errors occurred."
                };
                ApiProblemDetails.Enrich(context.HttpContext, problem);
                var result = new BadRequestObjectResult(problem);
                result.ContentTypes.Add("application/problem+json");
                return result;
            };
        });
        return services;
    }
}
