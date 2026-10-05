using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MyOnlineShop.Foundation;

internal sealed class ApiProblemDetailsFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is ObjectResult { Value: ProblemDetails problem })
            ApiProblemDetails.Enrich(context.HttpContext, problem);
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}
