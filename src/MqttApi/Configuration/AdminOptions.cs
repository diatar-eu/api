namespace MqttApi.Configuration;

public class AdminOptions
{
    public bool Enabled { get; set; } = true;
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5262;
    public string Usernames { get; set; } = string.Empty;
    public string CookieSecret { get; set; } = string.Empty;
    public int SessionMinutes { get; set; } = 480;
    public int LoginAttemptLimit { get; set; } = 5;
    public int LoginAttemptWindowMinutes { get; set; } = 5;
    public string AuditLogPath { get; set; } = string.Empty;
}
