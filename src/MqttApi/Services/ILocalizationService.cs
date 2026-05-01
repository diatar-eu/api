namespace MqttApi.Services;

public interface ILocalizationService
{
    string Get(string key, params object[] args);
}
