using MqttApi.Constants;

namespace MqttApi.Services;

public class UserManagementService(
    IDynsecService dynsec,
    IEmailService emailService) : IUserManagementService
{
    public async Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default) =>
        await dynsec.GetUserAsync(username, ct) != null;

    public async Task<string?> FindEmailOwnerAsync(string email, string? excludeUsername, CancellationToken ct = default)
    {
        var users = await dynsec.ListClientsDetailedAsync(ct);
        foreach (var user in users)
        {
            if (user.Username is null || string.IsNullOrEmpty(user.TextName)) continue;
            if (excludeUsername != null && string.Equals(user.Username, excludeUsername, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(user.TextName, email, StringComparison.OrdinalIgnoreCase))
                return user.Username;
        }
        return null;
    }

    public async Task<UserCreateStatus> CreateUserAsync(
        string username, string password, string email, bool skipVerification, CancellationToken ct = default)
    {
        if (await dynsec.GetUserAsync(username, ct) != null)
            return UserCreateStatus.UsernameTaken;

        if (await FindEmailOwnerAsync(email, excludeUsername: null, ct) != null)
            return UserCreateStatus.EmailTaken;

        if (skipVerification)
        {
            // A verified user needs its own role, otherwise it could not publish to Diatar/<username>/#
            var rolename = DynsecConstants.Acl.RolePrefix + username;
            await dynsec.CreateRoleAsync(rolename, username, ct);
            await dynsec.CreateUserAsync(username, password, email, [rolename], disabled: false, ct: ct);
            return UserCreateStatus.Created;
        }

        var token = Guid.NewGuid().ToString("N");
        await dynsec.CreateUserAsync(username, password, email, [], disabled: true, textDescription: token, ct: ct);
        await emailService.SendVerificationEmailAsync(email, username, token, ct);
        return UserCreateStatus.Created;
    }

    public async Task<bool> ResendVerificationAsync(string username, string email, CancellationToken ct = default)
    {
        var user = await dynsec.GetUserAsync(username, ct);
        if (user is null || !user.Disabled) return false;
        if (!string.Equals(user.TextName, email, StringComparison.OrdinalIgnoreCase)) return false;

        var token = Guid.NewGuid().ToString("N");
        await dynsec.SetVerificationTokenAsync(username, token, ct);
        await emailService.SendVerificationEmailAsync(email, username, token, ct);
        return true;
    }
}
