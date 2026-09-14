using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshDesktop.Domain;

public static class ConfigSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialize(AppConfig config)
    {
        var dto = ConfigDto.FromAppConfig(config);
        return JsonSerializer.Serialize(dto, Options);
    }

    public static AppConfig Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return AppConfig.CreateDefault();

        var dto = JsonSerializer.Deserialize<ConfigDto>(json, Options) ?? new ConfigDto();
        return dto.ToAppConfig();
    }
}

/// <summary>
/// JSON 读写用的扁平 DTO。<b>所有字段默认值必须从 <see cref="AppConfig.CreateDefault"/> 取</b>，
/// 不要在属性初始化器里另抄一份字面量 —— 抄一份就会漂移一份。
/// </summary>
/// <remarks>
/// 字段级默认值不是死代码：空白 JSON 会直接返回 <see cref="AppConfig.CreateDefault"/>，
/// 但**结构合法而字段缺失**的 JSON（手改过的 config.json、旧版本落盘的文件）会走
/// <c>Deserialize&lt;ConfigDto&gt;</c>，缺哪个字段就用哪个字段的默认值。
/// 一旦在这里另抄字面量就会与 CreateDefault() 漂移，表现为"改过默认值又变回去"。
/// 统一从 CreateDefault() 取值后，物理上只可能有一份真相。
/// </remarks>
internal sealed class ConfigDto
{
    // 单一真相源：把 CreateDefault() 的结果取一次，下面每个字段都从它落值。
    private static readonly AppConfig D = AppConfig.CreateDefault();

    public string DefaultUrl { get; set; } = D.DefaultUrl;
    public ServiceStrategy ServiceStrategy { get; set; } = D.ServiceStrategy;
    public string LaunchCommand { get; set; } = D.LaunchCommand;
    public string UrlExtractRegex { get; set; } = D.UrlExtractRegex;
    public string SuccessMarkerRegex { get; set; } = D.SuccessMarkerRegex;
    public string WorkingDirectory { get; set; } = D.WorkingDirectory;
    public ChromeButtonVisibility ChromeButtonsDefault { get; set; } = D.ChromeButtonsDefault;
    public string ChromeButtonIconColor { get; set; } = D.ChromeButtonIconColor;
    public string ChromeButtonBackground { get; set; } = D.ChromeButtonBackground;
    public int ChromeHoverDelayMs { get; set; } = D.ChromeHoverDelayMs;
    public bool DragStripEnabled { get; set; } = D.DragStripEnabled;
    public int DragStripLeftInset { get; set; } = D.DragStripLeftInset;
    public int DragStripRightInset { get; set; } = D.DragStripRightInset;
    public int DragStripHeight { get; set; } = D.DragStripHeight;
    public string DragStripColor { get; set; } = D.DragStripColor;
    public double DragStripOpacity { get; set; } = D.DragStripOpacity;
    public int WindowX { get; set; } = D.WindowX;
    public int WindowY { get; set; } = D.WindowY;
    public int WindowWidth { get; set; } = D.WindowWidth;
    public int WindowHeight { get; set; } = D.WindowHeight;
    public bool WindowMaximized { get; set; } = D.WindowMaximized;

    public static ConfigDto FromAppConfig(AppConfig c) => new()
    {
        DefaultUrl = c.DefaultUrl,
        ServiceStrategy = c.ServiceStrategy,
        LaunchCommand = c.LaunchCommand,
        UrlExtractRegex = c.UrlExtractRegex,
        SuccessMarkerRegex = c.SuccessMarkerRegex,
        WorkingDirectory = c.WorkingDirectory,
        ChromeButtonsDefault = c.ChromeButtonsDefault,
        ChromeButtonIconColor = c.ChromeButtonIconColor,
        ChromeButtonBackground = c.ChromeButtonBackground,
        ChromeHoverDelayMs = c.ChromeHoverDelayMs,
        DragStripEnabled = c.DragStripEnabled,
        DragStripLeftInset = c.DragStripLeftInset,
        DragStripRightInset = c.DragStripRightInset,
        DragStripHeight = c.DragStripHeight,
        DragStripColor = c.DragStripColor,
        DragStripOpacity = c.DragStripOpacity,
        WindowX = c.WindowX,
        WindowY = c.WindowY,
        WindowWidth = c.WindowWidth,
        WindowHeight = c.WindowHeight,
        WindowMaximized = c.WindowMaximized,
    };

    public AppConfig ToAppConfig() => new(
        DefaultUrl,
        ServiceStrategy,
        LaunchCommand,
        UrlExtractRegex,
        SuccessMarkerRegex,
        WorkingDirectory,
        ChromeButtonsDefault,
        ChromeButtonIconColor,
        ChromeButtonBackground,
        ChromeHoverDelayMs,
        DragStripEnabled,
        DragStripLeftInset,
        DragStripRightInset,
        DragStripHeight,
        DragStripColor,
        DragStripOpacity,
        WindowX,
        WindowY,
        WindowWidth,
        WindowHeight,
        WindowMaximized);
}
