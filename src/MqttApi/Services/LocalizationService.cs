using System.Text.Json;

namespace MqttApi.Services;

public class LocalizationService(IHttpContextAccessor httpContextAccessor) : ILocalizationService
{
    private static readonly Dictionary<string, Dictionary<string, string>> _translations = LoadAll();

    public string Get(string key, params object[] args)
    {
        var lang = ResolveLanguage();
        string? value = null;
        if (!_translations.TryGetValue(lang, out var dict) || !dict.TryGetValue(key, out value))
            _translations.GetValueOrDefault("hu")?.TryGetValue(key, out value);

        if (value is null) return key;
        return args.Length > 0 ? string.Format(value, args) : value;
    }

    private string ResolveLanguage()
    {
        var header = httpContextAccessor.HttpContext?.Request.Headers.AcceptLanguage.ToString();
        if (string.IsNullOrWhiteSpace(header)) return "hu";

        var lang = header.Split(',')[0].Split(';')[0].Split('-')[0].Trim().ToLowerInvariant();
        return _translations.ContainsKey(lang) ? lang : "hu";
    }

    private static Dictionary<string, Dictionary<string, string>> LoadAll()
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        var dir = Path.Combine(AppContext.BaseDirectory, "Localization");
        if (!Directory.Exists(dir)) return result;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var lang = Path.GetFileNameWithoutExtension(file);
            var json = File.ReadAllText(file);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict is not null) result[lang] = dict;
        }
        return result;
    }
}
