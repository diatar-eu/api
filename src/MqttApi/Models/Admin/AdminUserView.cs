using MqttApi.Models.Dynsec;

namespace MqttApi.Models.Admin;

public static class UserStatus
{
    public const string Verified = "verified";
    public const string Pending  = "pending";
    public const string Disabled = "disabled";
}

public class AdminUserView
{
    public string Username { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Status { get; set; } = UserStatus.Verified;
    public string[] Roles { get; set; } = [];
    public bool IsDiatarUser { get; set; }

    public static AdminUserView From(DynsecClientData user) => new()
    {
        Username = user.Username ?? string.Empty,
        Email = string.IsNullOrWhiteSpace(user.TextName) ? null : user.TextName,
        Status = user.Disabled
            ? user.HasPendingVerification() ? UserStatus.Pending : UserStatus.Disabled
            : UserStatus.Verified,
        Roles = user.Roles?.Select(r => r.Rolename).OrderBy(r => r, StringComparer.Ordinal).ToArray() ?? [],
        IsDiatarUser = user.HasUserRole()
    };
}
