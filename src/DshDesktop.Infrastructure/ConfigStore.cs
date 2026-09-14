using System.IO;
using DshDesktop.Domain;

namespace DshDesktop.Infrastructure;

public sealed class ConfigStore
{
    private readonly string _path;

    public ConfigStore(string configPath)
    {
        _path = configPath;
    }

    public static string GetDefaultPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "config.json");
    }

    public AppConfig Load()
    {
        if (!File.Exists(_path))
            return AppConfig.CreateDefault();

        var json = File.ReadAllText(_path);
        return ConfigSerializer.Deserialize(json);
    }

    public void Save(AppConfig config)
    {
        var json = ConfigSerializer.Serialize(config);
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(_path, json);
    }

    public string DirectoryPath =>
        Path.GetDirectoryName(_path) ?? AppContext.BaseDirectory;

    public string ConfigPath => _path;
}
