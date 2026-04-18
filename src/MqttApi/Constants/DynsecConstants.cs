namespace MqttApi.Constants;

public static class DynsecConstants
{
    public const string CommandTopic = "$CONTROL/dynamic-security/v1";

    public static class Commands
    {
        public const string CreateClient = "createClient";
        public const string ModifyClient = "modifyClient";
        public const string DeleteClient = "deleteClient";
        public const string GetClient    = "getClient";
        public const string ListClients  = "listClients";
        public const string CreateRole   = "createRole";
    }

    public static class Errors
    {
        public const string ClientAlreadyExists = "Client already exists";
        public const string ClientNotFound      = "Client not found";
    }

    public static class Acl
    {
        public const string PublishClientSend = "publishClientSend";
        public const string RolePrefix        = "s-";
        public const string TopicTemplate     = "Diatar/{0}/#";
    }
}
