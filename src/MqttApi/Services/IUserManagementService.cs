namespace MqttApi.Services;

public enum UserCreateStatus
{
    Created,
    UsernameTaken,
    EmailTaken
}

public interface IUserManagementService
{
    Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default);

    // Returns the username that owns the email, or null if the email is free.
    // Pass excludeUsername to skip the current user (used when changing their own email).
    Task<string?> FindEmailOwnerAsync(string email, string? excludeUsername, CancellationToken ct = default);

    // Creates a disabled user holding a verification token and mails that token out.
    // When skipVerification is set the user is created as a ready to use, verified one.
    Task<UserCreateStatus> CreateUserAsync(
        string username, string password, string email, bool skipVerification, CancellationToken ct = default);

    // Re-sends the verification email for an unverified account. Returns false when
    // there is nothing to send (unknown user, already verified, or email mismatch).
    Task<bool> ResendVerificationAsync(string username, string email, CancellationToken ct = default);
}
