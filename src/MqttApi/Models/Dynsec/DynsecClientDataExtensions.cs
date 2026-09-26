using MqttApi.Constants;

namespace MqttApi.Models.Dynsec;

public static class DynsecClientDataExtensions
{
    // A self service regisztráció először role nélkül, disabled klienssel létrehozza a
    // felhasználót (ez tartja a verification tokent), és csak a megerősítéskor kapja
    // meg a saját szerepét. Ezért a szerep hiánya önmagában nem jelenti, hogy nem
    // Diatar user: egy megerősítésre váró felhasználónál ez a normális állapot.
    public static bool HasUserRole(this DynsecClientData? user) =>
        user?.Roles?.Any(role => role.Rolename.StartsWith(DynsecConstants.Acl.RolePrefix, StringComparison.Ordinal)) == true;

    // Igazolt Diatar user: vagy már van saját szerepe, vagy még megerősítésre vár
    // (disabled, a token a TextDescription mezőben). A tisztán MQTT klienseknek
    // (a broker saját adminja, egyéb MQTT felhasználók) egyik sem igaz.
    public static bool IsDiatarUser(this DynsecClientData? user) =>
        user is not null && (user.HasUserRole() || user.HasPendingVerification());

    // Disabled, de a token még a helyén van: a "megerősítésre vár" állapot.
    public static bool HasPendingVerification(this DynsecClientData? user) =>
        user is not null
        && user.Disabled
        && !string.IsNullOrWhiteSpace(user.TextName)
        && !string.IsNullOrWhiteSpace(user.TextDescription);
}
