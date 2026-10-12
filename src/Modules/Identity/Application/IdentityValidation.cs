using System.ComponentModel.DataAnnotations;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.BuildingBlocks.Abstractions.Localization;

namespace MyOnlineShop.Identity.Application;

public static class IdentityValidation
{
    public static void Validate(object command)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(command, new ValidationContext(command), results, true);
        if (command is RegisterCommand registration)
        {
            if (string.IsNullOrWhiteSpace(registration.FirstName))
                results.Add(new ValidationResult(string.Format(ValidationMessages.Required, ValidationMessages.FirstName), [nameof(registration.FirstName)]));
            if (string.IsNullOrWhiteSpace(registration.LastName))
                results.Add(new ValidationResult(string.Format(ValidationMessages.Required, ValidationMessages.LastName), [nameof(registration.LastName)]));
        }
        if (results.Count > 0)
        {
            var errors = results.SelectMany(result => result.MemberNames.DefaultIfEmpty(string.Empty)
                    .Select(member => (Member: member, Message: result.ErrorMessage ?? ValidationMessages.InvalidValue)))
                .GroupBy(result => result.Member)
                .ToDictionary(group => group.Key, group => group.Select(result => result.Message).Distinct().ToArray());
            throw new IdentityException("validation_error", 400, ErrorMessages.HttpError("validation_error"), errors);
        }
    }
}
