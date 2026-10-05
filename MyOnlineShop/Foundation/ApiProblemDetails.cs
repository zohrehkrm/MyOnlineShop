using Microsoft.AspNetCore.Mvc;

namespace MyOnlineShop.Foundation;

internal static class ApiProblemDetails
{
    public static void Enrich(HttpContext context, ProblemDetails problem)
    {
        problem.Extensions["correlationId"] = context.TraceIdentifier;
        problem.Extensions["code"] = problem switch
        {
            ValidationProblemDetails => "validation_error",
            { Status: 400 } => "bad_request",
            { Status: 401 } => "unauthorized",
            { Status: 403 } => "forbidden",
            { Status: 404 } => "not_found",
            { Status: 405 } => "method_not_allowed",
            { Status: >= 500 } => "internal_error",
            _ => "request_error"
        };
    }
}
