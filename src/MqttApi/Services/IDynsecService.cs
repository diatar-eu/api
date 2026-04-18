using MqttApi.Models.Dynsec;

namespace MqttApi.Services;

public interface IDynsecService
{
    Task CreateUserAsync(string username, string password, string? email, string[] roles,
        bool disabled = false, string? textDescription = null, CancellationToken ct = default);
    Task VerifyUserAsync(string username, string token, CancellationToken ct = default);
    Task CreateRoleAsync(string rolename, string ownerUsername, CancellationToken ct = default);
    Task DeleteUserAsync(string username, CancellationToken ct = default);
    Task ChangePasswordAsync(string username, string newPassword, CancellationToken ct = default);
    Task ChangeEmailAsync(string username, string newEmail, string token, CancellationToken ct = default);
    Task ChangeUsernameAsync(string username, string newUsername, string newPassword, CancellationToken ct = default);
    Task<DynsecClientData?> GetUserAsync(string username, CancellationToken ct = default);
    Task SetVerificationTokenAsync(string username, string token, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListClientNamesAsync(CancellationToken ct = default);
}
