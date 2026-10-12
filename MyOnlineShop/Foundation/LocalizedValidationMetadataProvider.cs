using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using MyOnlineShop.BuildingBlocks.Abstractions.Localization;

namespace MyOnlineShop.Foundation;

internal sealed class LocalizedValidationMetadataProvider : IValidationMetadataProvider
{
    public void CreateValidationMetadata(ValidationMetadataProviderContext context)
    {
        foreach (var attribute in context.ValidationMetadata.ValidatorMetadata.OfType<ValidationAttribute>())
        {
            if (attribute.ErrorMessageResourceType is not null || attribute.ErrorMessage is not null) continue;
            var key = attribute switch
            {
                RequiredAttribute => nameof(ValidationMessages.Required),
                StringLengthAttribute => nameof(ValidationMessages.StringLength),
                MaxLengthAttribute => nameof(ValidationMessages.MaxLength),
                MinLengthAttribute => nameof(ValidationMessages.MinLength),
                EmailAddressAttribute => nameof(ValidationMessages.EmailAddress),
                RangeAttribute => nameof(ValidationMessages.Range),
                RegularExpressionAttribute => nameof(ValidationMessages.RegularExpression),
                _ => null
            };
            if (key is null) continue;
            attribute.ErrorMessageResourceType = typeof(ValidationMessages);
            attribute.ErrorMessageResourceName = key;
        }
    }
}
