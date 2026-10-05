using System.ComponentModel.DataAnnotations;
using MyOnlineShop.Identity.Contracts;

namespace MyOnlineShop.Identity.Application;

public static class IdentityValidation
{
    public static void Validate(object command)
    {
        if (!Validator.TryValidateObject(command, new ValidationContext(command), [], true))
            throw new IdentityException("validation_error", 400, "Identity input is invalid.");
        if (command is RegisterCommand registration)
        {
            var password = registration.Password;
            if (!password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit) ||
                !password.Any(character => !char.IsLetterOrDigit(character) && !char.IsWhiteSpace(character)))
                throw new IdentityException("validation_error", 400,
                    "Password must contain uppercase and lowercase letters, a digit, and a symbol.");
            if (string.IsNullOrWhiteSpace(registration.FirstName) || string.IsNullOrWhiteSpace(registration.LastName))
                throw new IdentityException("validation_error", 400, "First name and last name are required.");
        }
    }
}
