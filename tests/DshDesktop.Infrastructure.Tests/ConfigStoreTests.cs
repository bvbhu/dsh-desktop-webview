using System.IO;
using DshDesktop.Domain;
using DshDesktop.Infrastructure;
using FluentAssertions;

namespace DshDesktop.Infrastructure.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _tempPath;

    public ConfigStoreTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"cfg_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath))
            File.Delete(_tempPath);
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsDefaults()
    {
        var store = new ConfigStore(_tempPath);
        var cfg = store.Load();
        cfg.Should().BeEquivalentTo(AppConfig.CreateDefault());
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var store = new ConfigStore(_tempPath);
        var original = AppConfig.CreateDefault() with
        {
            DefaultUrl = "http://localhost:7777/",
            LaunchCommand = "myapp serve --port 7777",
            ChromeHoverDelayMs = 1500,
        };

        store.Save(original);
        var loaded = store.Load();

        loaded.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Save_OverwritesEntireFile()
    {
        var store = new ConfigStore(_tempPath);
        var first = AppConfig.CreateDefault() with { DefaultUrl = "http://first/" };
        var second = AppConfig.CreateDefault() with { DefaultUrl = "http://second/" };

        store.Save(first);
        store.Save(second);

        var loaded = store.Load();
        loaded.DefaultUrl.Should().Be("http://second/");
    }

    [Fact]
    public void Save_CreatesParentDirectory_IfMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"subdir_{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "config.json");
        try
        {
            var store = new ConfigStore(path);
            store.Save(AppConfig.CreateDefault());
            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Save_ProducesReadableCamelCaseJson()
    {
        var store = new ConfigStore(_tempPath);
        store.Save(AppConfig.CreateDefault());

        var json = File.ReadAllText(_tempPath);
        json.Should().Contain("\"defaultUrl\"");
        json.Should().Contain("\"serviceStrategy\"");
        json.Should().Contain("\"dragStripOpacity\"");
    }

    [Fact]
    public void Load_HandEditedJson_WithMissingFields()
    {
        File.WriteAllText(_tempPath, """{"defaultUrl":"http://x/","launchCommand":"app"}""");
        var store = new ConfigStore(_tempPath);
        var cfg = store.Load();

        cfg.DefaultUrl.Should().Be("http://x/");
        cfg.LaunchCommand.Should().Be("app");
        cfg.ServiceStrategy.Should().Be(ServiceStrategy.ProbeThenStart);
    }

    [Fact]
    public void DirectoryPath_ReturnsContainingFolder()
    {
        var store = new ConfigStore(_tempPath);
        store.DirectoryPath.Should().Be(Path.GetDirectoryName(_tempPath));
    }
}
