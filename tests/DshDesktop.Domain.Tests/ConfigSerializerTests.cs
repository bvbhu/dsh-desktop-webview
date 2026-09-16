using DshDesktop.Domain;
using FluentAssertions;
using System.Text.Json;

namespace DshDesktop.Domain.Tests;

public class ConfigSerializerTests
{
    [Fact]
    public void Serialize_Default_ProducesCamelCaseJson()
    {
        var json = ConfigSerializer.Serialize(AppConfig.CreateDefault());

        json.Should().Contain("\"defaultUrl\"");
        json.Should().Contain("\"serviceStrategy\"");
        json.Should().Contain("\"launchCommand\"");
        json.Should().Contain("\"dragStripOpacity\"");
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = AppConfig.CreateDefault() with
        {
            DefaultUrl = "http://localhost:8080/",
            ServiceStrategy = ServiceStrategy.AlwaysStart,
            LaunchCommand = "myapp serve",
            ChromeHoverDelayMs = 2000,
            DragStripOpacity = 0.5,
            WindowMaximized = true,
        };

        var json = ConfigSerializer.Serialize(original);
        var restored = ConfigSerializer.Deserialize(json);

        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Deserialize_EmptyString_ReturnsDefaults()
    {
        var cfg = ConfigSerializer.Deserialize("");
        cfg.Should().BeEquivalentTo(AppConfig.CreateDefault());
    }

    [Fact]
    public void Deserialize_NullString_ReturnsDefaults()
    {
        var cfg = ConfigSerializer.Deserialize(null!);
        cfg.Should().BeEquivalentTo(AppConfig.CreateDefault());
    }

    [Fact]
    public void Deserialize_Whitespace_ReturnsDefaults()
    {
        var cfg = ConfigSerializer.Deserialize("   ");
        cfg.Should().BeEquivalentTo(AppConfig.CreateDefault());
    }

    [Fact]
    public void Deserialize_PartialJson_MissingFieldsUseDefaults()
    {
        var json = """{"defaultUrl":"http://x/"}""";
        var cfg = ConfigSerializer.Deserialize(json);

        cfg.DefaultUrl.Should().Be("http://x/");
        cfg.ServiceStrategy.Should().Be(ServiceStrategy.ProbeThenStart);
        cfg.LaunchCommand.Should().Be("dsh web --no-open");
        cfg.DragStripEnabled.Should().BeTrue();
    }

    /// <summary>
    /// 缺字段的 JSON 必须逐字段等于 <see cref="AppConfig.CreateDefault"/>。
    /// </summary>
    /// <remarks>
    /// 这是防"默认值漂移"的哨兵。<c>ConfigSerializer</c> 内部有一份 DTO，
    /// 它自己的字段默认值曾经和 <c>CreateDefault()</c> 分家（三键停在 Hidden、
    /// 左 inset 停在 450、颜色停在 #FF0000），而当时那条只断言 4 个字段的
    /// partial-JSON 测试完全没发现。这里改成整体等价断言：今后往 AppConfig
    /// 加字段、改默认值，只要忘了同步 DTO，这条就会红。
    /// </remarks>
    [Fact]
    public void Deserialize_PartialJson_AllMissingFieldsEqualCreateDefault()
    {
        var cfg = ConfigSerializer.Deserialize("""{"defaultUrl":"http://x/"}""");
        var expected = AppConfig.CreateDefault() with { DefaultUrl = "http://x/" };

        cfg.Should().BeEquivalentTo(expected);
    }

    /// <summary>
    /// 空对象 <c>{}</c> 是完全合法的 JSON，会走 DTO 分支 —— 结果必须就是默认配置。
    /// </summary>
    [Fact]
    public void Deserialize_EmptyObject_EqualsCreateDefault()
    {
        var cfg = ConfigSerializer.Deserialize("{}");
        cfg.Should().BeEquivalentTo(AppConfig.CreateDefault());
    }

    /// <summary>
    /// 单独锁住本次改过的四个默认值，失败信息比整体等价断言更直白。
    /// </summary>
    [Fact]
    public void Deserialize_PartialJson_UsesUpdatedDefaultsForRecentlyChangedFields()
    {
        var cfg = ConfigSerializer.Deserialize("{}");

        cfg.DragStripOpacity.Should().Be(0.0);
        cfg.ChromeButtonsDefault.Should().Be(ChromeButtonVisibility.Shown);
        cfg.SuccessMarkerRegex.Should().BeEmpty();
        cfg.WindowWidth.Should().Be(1500);
        cfg.WindowHeight.Should().Be(750);
    }

    [Fact]
    public void Deserialize_UnknownFields_Ignored()
    {
        var json = """{"defaultUrl":"http://x/","unknownField":123}""";
        var act = () => ConfigSerializer.Deserialize(json);
        act.Should().NotThrow();
    }

    [Fact]
    public void Serialize_DoesNotContainUnknownFields()
    {
        var json = ConfigSerializer.Serialize(AppConfig.CreateDefault());
        var doc = JsonDocument.Parse(json);
        doc.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Should().NotContain("unknownField");
    }

    [Fact]
    public void Deserialize_StrategyS1_RoundTrips()
    {
        var cfg = AppConfig.CreateDefault() with { ServiceStrategy = ServiceStrategy.NeverStart };
        var json = ConfigSerializer.Serialize(cfg);
        var restored = ConfigSerializer.Deserialize(json);
        restored.ServiceStrategy.Should().Be(ServiceStrategy.NeverStart);
    }

    /// <summary>默认仍用合成宿主；开了才走 HwndHost（文件拖放需要它）。</summary>
    [Fact]
    public void UseHwndHost_DefaultsFalse_AndRoundTrips()
    {
        AppConfig.CreateDefault().UseHwndHost.Should().BeFalse();
        ConfigSerializer.Serialize(AppConfig.CreateDefault()).Should().Contain("\"useHwndHost\"");

        var cfg = AppConfig.CreateDefault() with { UseHwndHost = true };
        ConfigSerializer.Deserialize(ConfigSerializer.Serialize(cfg)).UseHwndHost.Should().BeTrue();
    }

    /// <summary>旧版 config.json 没有该字段，必须回落成 false 而不是崩或误开。</summary>
    [Fact]
    public void Deserialize_LegacyJsonWithoutUseHwndHost_DefaultsFalse()
    {
        var json = """{"defaultUrl":"http://x/","windowMaximized":false}""";
        ConfigSerializer.Deserialize(json).UseHwndHost.Should().BeFalse();
    }

    [Fact]
    public void Serialize_OverwritesCompletely()
    {
        var original = AppConfig.CreateDefault() with
        {
            ChromeButtonIconColor = "#ABCDEF",
            ChromeButtonBackground = " #123456 ",
        };
        var json = ConfigSerializer.Serialize(original);
        var restored = ConfigSerializer.Deserialize(json);

        restored.ChromeButtonIconColor.Should().Be("#ABCDEF");
    }
}
