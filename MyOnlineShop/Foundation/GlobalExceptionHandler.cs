using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Abstractions.Localization;

namespace MyOnlineShop.Foundation;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var applicationError = exception as IApplicationError;
        var status = applicationError?.StatusCode ?? (exception is BadHttpRequestException badRequest
            ? badRequest.StatusCode : StatusCodes.Status500InternalServerError);
        // Exception messages may contain secrets or personal data; log safe metadata only.
        logger.LogError("Request failed with {ExceptionType} and {StatusCode}",
            exception.GetType().Name, status);
        ProblemDetails problem = applicationError is IApplicationValidationError { Errors.Count: > 0 } validation
            ? new ValidationProblemDetails(validation.Errors.ToDictionary(entry => entry.Key, entry => entry.Value))
            : new ProblemDetails();
        problem.Status = status;
        problem.Type = $"https://httpstatuses.io/{status}";
        ApiProblemDetails.Enrich(context, problem);
        if (applicationError is not null)
        {
            problem.Extensions["code"] = applicationError.Code;
            problem.Title = ErrorMessages.ApplicationError(applicationError.Code, status);
        }
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(problem, problem.GetType(), options: null,
            contentType: "application/problem+json", cancellationToken: cancellationToken);
        return true;
    }
}
