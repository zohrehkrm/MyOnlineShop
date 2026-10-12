using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Localization;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Abstractions.Localization;

namespace MyOnlineShop.Foundation;

public static class DependencyInjection
{
    public static IServiceCollection AddApiFoundation(this IServiceCollection services)
    {
        services.AddLocalization();
        services.Configure<RequestLocalizationOptions>(options =>
        {
            var cultures = new[] { "fa-IR", "fa", "en-US", "en" };
            options.SetDefaultCulture("fa-IR").AddSupportedCultures(cultures).AddSupportedUICultures(cultures);
            options.ApplyCurrentCultureToResponseHeaders = true;
        });
        services.AddHttpContextAccessor();
        services.AddScoped<IRequestContext, HttpRequestContext>();
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            ApiProblemDetails.Enrich(context.HttpContext, context.ProblemDetails));
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddControllers(options =>
        {
            options.Filters.Add<ApiProblemDetailsFilter>();
            options.ModelMetadataDetailsProviders.Add(new LocalizedValidationMetadataProvider());
            var messages = options.ModelBindingMessageProvider;
            messages.SetAttemptedValueIsInvalidAccessor((value, field) => ValidationMessages.InvalidValue);
            messages.SetNonPropertyAttemptedValueIsInvalidAccessor(value => ValidationMessages.InvalidValue);
            messages.SetUnknownValueIsInvalidAccessor(field => ValidationMessages.InvalidValue);
            messages.SetNonPropertyUnknownValueIsInvalidAccessor(() => ValidationMessages.InvalidValue);
            messages.SetValueIsInvalidAccessor(value => ValidationMessages.InvalidValue);
            messages.SetValueMustNotBeNullAccessor(value => ValidationMessages.InvalidValue);
            messages.SetValueMustBeANumberAccessor(field => ValidationMessages.InvalidValue);
            messages.SetNonPropertyValueMustBeANumberAccessor(() => ValidationMessages.InvalidValue);
            messages.SetMissingBindRequiredValueAccessor(field => ValidationMessages.InvalidValue);
            messages.SetMissingKeyOrValueAccessor(() => ValidationMessages.InvalidValue);
            messages.SetMissingRequestBodyRequiredValueAccessor(() => ValidationMessages.InvalidBody);
        })
            .ConfigureApiBehaviorOptions(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                // Only validation messages are exposed; parser exceptions can contain submitted values.
                var errors = context.ModelState.Where(entry => entry.Value?.Errors.Count > 0)
                    .ToDictionary(entry => entry.Key, entry => entry.Value!.Errors.Select(error =>
                        error.Exception is not null || entry.Key.StartsWith('$')
                            ? ValidationMessages.InvalidBody
                            : string.IsNullOrEmpty(error.ErrorMessage) ? ValidationMessages.InvalidValue : error.ErrorMessage)
                        .Distinct().ToArray());
                var problem = new ValidationProblemDetails(errors)
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = ErrorMessages.HttpError("validation_error")
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
