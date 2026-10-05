namespace MyOnlineShop.Identity.Domain;

public enum UserStatus { Active = 1, Inactive = 2 }

public sealed class User
{
    private User() { }
    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string NormalizedEmail { get; private set; } = string.Empty;
    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;
    public string? PhoneNumber { get; private set; }
    public string PasswordHash { get; private set; } = string.Empty;
    public UserStatus Status { get; private set; }
    public Guid SecurityStamp { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public int FailedLoginAttempts { get; private set; }
    public DateTimeOffset? LockoutUntilUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static User Create(string email, string firstName, string lastName, string? phoneNumber, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Trim().Length > 254 ||
            string.IsNullOrWhiteSpace(firstName) || firstName.Trim().Length > 100 ||
            string.IsNullOrWhiteSpace(lastName) || lastName.Trim().Length > 100)
            throw new ArgumentException("Required profile fields are invalid.");
        return new User
        {
            Id = Guid.NewGuid(), Email = email.Trim(), NormalizedEmail = NormalizeEmail(email),
            FirstName = firstName.Trim(), LastName = lastName.Trim(),
            PhoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim(),
            Status = UserStatus.Active, SecurityStamp = Guid.NewGuid(), CreatedAtUtc = now
        };
    }

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();
    public void SetPasswordHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) throw new ArgumentException("A password hash is required.");
        PasswordHash = hash;
    }
    public bool CanLogin(DateTimeOffset now) => Status == UserStatus.Active && !(LockoutUntilUtc > now);
    public void RecordFailedLogin(DateTimeOffset now, int maximumAttempts, TimeSpan lockout)
    {
        if (LockoutUntilUtc is not null && LockoutUntilUtc <= now) FailedLoginAttempts = 0;
        FailedLoginAttempts++;
        if (FailedLoginAttempts >= maximumAttempts) LockoutUntilUtc = now.Add(lockout);
    }
    public void RecordSuccessfulLogin() { FailedLoginAttempts = 0; LockoutUntilUtc = null; }
    public void ChangeStatus(UserStatus status)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        Status = status;
        SecurityStamp = Guid.NewGuid();
    }
    public void InvalidateAccessTokens() => SecurityStamp = Guid.NewGuid();
}
