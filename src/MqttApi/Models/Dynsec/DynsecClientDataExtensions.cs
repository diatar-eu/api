using MqttApi.Constants;

namespace MqttApi.Models.Dynsec;

public static class DynsecClientDataExtensions
{
    // Every self registered Diatar user owns a role named after their username
    // (see DynsecConstants.Acl.RolePrefix). The broker's own admin client does not.
    public static bool HasUserRole(this DynsecClientData? user) =>
        user?.Roles?.Any(role => role.Rolename.StartsWith(DynsecConstants.Acl.RolePrefix, StringComparison.Ordinal)) == true;

    public static bool HasPendingVerification(this DynsecClientData? user) =>
        user is not null
        && user.Disabled
        && !string.IsNullOrWhiteSpace(user.TextName)
        && !string.IsNullOrWhiteSpace(user.TextDescription);
}
