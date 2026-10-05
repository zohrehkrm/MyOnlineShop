using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MyOnlineShop.BuildingBlocks.Abstractions;

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
        var problem = new ProblemDetails
        {
            Status = status,
            Title = applicationError?.SafeMessage ?? (status == 500 ? "An unexpected error occurred." : "The request could not be processed."),
            Type = $"https://httpstatuses.io/{status}"
        };
        ApiProblemDetails.Enrich(context, problem);
        if (applicationError is not null) problem.Extensions["code"] = applicationError.Code;
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(problem, options: null,
            contentType: "application/problem+json", cancellationToken: cancellationToken);
        return true;
    }
}
