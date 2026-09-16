using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using DshDesktop.Domain;

namespace DshDesktop.Application;

public sealed class SettingsViewModel : ViewModelBase
{
    /// <summary>取消勾选「跟随主题」时给一个立即可见的白底，详见 <see cref="ChromeButtonBackgroundFollowTheme"/>。</summary>
    private const string DefaultChromeButtonBackground = "#FFFFFF";

    /// <summary>上次保存（或加载）的配置，作为「取消」的回退基线；保存成功后必须推进。</summary>
    private AppConfig _snapshot;

    private string _defaultUrl;
    private ServiceStrategy _serviceStrategy;
    private string _launchCommand;
    private string _urlExtractRegex;
    private string _successMarkerRegex;
    private string _workingDirectory;
    private ChromeButtonVisibility _chromeButtonsDefault;
    private string _chromeButtonIconColor;
    private string _chromeButtonBackground;
    private int _chromeHoverDelayMs;
    private bool _dragStripEnabled;
    private int _dragStripLeftInset;
    private int _dragStripRightInset;
    private int _dragStripHeight;
    private string _dragStripColor;
    private double _dragStripOpacity;
    private string? _validationError;

    public string DefaultUrl { get => _defaultUrl; set => Set(ref _defaultUrl, value); }

    public ServiceStrategy ServiceStrategy
    {
        get => _serviceStrategy;
        set
        {
            Set(ref _serviceStrategy, value);
            // 派生视图跟着刷：选「不启动」要实时藏起启动配置
            Raise(nameof(ServiceNeverStart));
        }
    }

    /// <summary>是否「不启动，直接打开url」。为 true 时启动策略卡片里的工作目录 / 启动命令 / 两个正则整块隐藏。</summary>
    public bool ServiceNeverStart => _serviceStrategy == ServiceStrategy.NeverStart;

    public string LaunchCommand { get => _launchCommand; set => Set(ref _launchCommand, value); }
    public string UrlExtractRegex { get => _urlExtractRegex; set => Set(ref _urlExtractRegex, value); }
    public string SuccessMarkerRegex { get => _successMarkerRegex; set => Set(ref _successMarkerRegex, value); }
    public string WorkingDirectory { get => _workingDirectory; set => Set(ref _workingDirectory, value); }
    public ChromeButtonVisibility ChromeButtonsDefault { get => _chromeButtonsDefault; set => Set(ref _chromeButtonsDefault, value); }
    public string ChromeButtonIconColor { get => _chromeButtonIconColor; set => Set(ref _chromeButtonIconColor, value); }
    public string ChromeButtonBackground
    {
        get => _chromeButtonBackground;
        set
        {
            Set(ref _chromeButtonBackground, value);
            // 「跟随主题」是派生视图，字段一动就得刷新
            Raise(nameof(ChromeButtonBackgroundFollowTheme));
        }
    }

    /// <summary>
    /// 「三键底色」是否跟随主题 —— 即 <see cref="ChromeButtonBackground"/> 是否为空。
    /// 做成派生属性而非另存 bool：底色的「留空 = 跟随主题」语义已由 <see cref="AppConfig.ChromeButtonBackground"/> 单独承载，
    /// 多存一个 bool 就多一处可能不同步的状态。
    /// </summary>
    public bool ChromeButtonBackgroundFollowTheme
    {
        get => string.IsNullOrWhiteSpace(_chromeButtonBackground);
        set
        {
            if (value == ChromeButtonBackgroundFollowTheme)
                return;

            ChromeButtonBackground = value ? string.Empty : DefaultChromeButtonBackground;
        }
    }
    public int ChromeHoverDelayMs { get => _chromeHoverDelayMs; set => Set(ref _chromeHoverDelayMs, value); }
    public bool DragStripEnabled { get => _dragStripEnabled; set => Set(ref _dragStripEnabled, value); }
    public int DragStripLeftInset { get => _dragStripLeftInset; set => Set(ref _dragStripLeftInset, value); }
    public int DragStripRightInset { get => _dragStripRightInset; set => Set(ref _dragStripRightInset, value); }
    public int DragStripHeight { get => _dragStripHeight; set => Set(ref _dragStripHeight, value); }
    // 颜色 / 不透明度一动，合成值 DragStripArgb 跟着变（颜色选择器绑的是它）
    public string DragStripColor
    {
        get => _dragStripColor;
        set
        {
            Set(ref _dragStripColor, value);
            Raise(nameof(DragStripArgb));
        }
    }

    public double DragStripOpacity
    {
        get => _dragStripOpacity;
        set
        {
            Set(ref _dragStripOpacity, value);
            Raise(nameof(DragStripArgb));
        }
    }

    /// <summary>
    /// 拖动层颜色 + 不透明度合成的 ARGB，供「能选透明度的颜色选择器」使用（颜色与透明度共用一个选择器，不给输入框）。
    /// 合成的 <see cref="AppConfig"/> 字段不变，拆/合只发生在这对访问器里；非法值原样透出，交给校验器报错。
    /// </summary>
    public string DragStripArgb
    {
        get
        {
            if (!HexColor.TryParse(_dragStripColor, out _, out var r, out var g, out var b))
                return _dragStripColor;

            var a = (byte)Math.Clamp(Math.Round(_dragStripOpacity * 255), 0, 255);
            return $"#{a:X2}{r:X2}{g:X2}{b:X2}";
        }
        set
        {
            if (!HexColor.TryParse(value, out var a, out var r, out var g, out var b))
            {
                Set(ref _dragStripColor, value ?? string.Empty, nameof(DragStripColor));
                Raise(nameof(DragStripArgb));
                return;
            }

            // 名字必须显式给：否则 CallerMemberName 发出 DragStripArgb 通知，颜色字段的绑定收不到
            Set(ref _dragStripColor, $"#{r:X2}{g:X2}{b:X2}", nameof(DragStripColor));
            Set(ref _dragStripOpacity, Math.Clamp(Math.Round(a / 255.0, 4), 0, 1), nameof(DragStripOpacity));
            Raise(nameof(DragStripArgb));
        }
    }
    public string? ValidationError { get => _validationError; set => Set(ref _validationError, value); }

    public SettingsViewModel(AppConfig config)
    {
        _snapshot = config;
        ApplyConfig(config);
    }

    // Nullable 严格模式下，赋值拆进方法后编译器看不到字段已初始化，需显式声明。
    [MemberNotNull(nameof(_defaultUrl), nameof(_launchCommand), nameof(_urlExtractRegex),
        nameof(_successMarkerRegex), nameof(_workingDirectory), nameof(_chromeButtonIconColor),
        nameof(_chromeButtonBackground), nameof(_dragStripColor))]
    private void ApplyConfig(AppConfig config)
    {
        _defaultUrl = config.DefaultUrl;
        _serviceStrategy = config.ServiceStrategy;
        _launchCommand = config.LaunchCommand;
        _urlExtractRegex = config.UrlExtractRegex;
        _successMarkerRegex = config.SuccessMarkerRegex;
        _workingDirectory = config.WorkingDirectory;
        _chromeButtonsDefault = config.ChromeButtonsDefault;
        _chromeButtonIconColor = config.ChromeButtonIconColor;
        _chromeButtonBackground = config.ChromeButtonBackground;
        _chromeHoverDelayMs = config.ChromeHoverDelayMs;
        _dragStripEnabled = config.DragStripEnabled;
        _dragStripLeftInset = config.DragStripLeftInset;
        _dragStripRightInset = config.DragStripRightInset;
        _dragStripHeight = config.DragStripHeight;
        _dragStripColor = config.DragStripColor;
        _dragStripOpacity = config.DragStripOpacity;
    }

    /// <summary>保存成功后把当前配置立为新基线。</summary>
    public void AcceptSnapshot(AppConfig config) => _snapshot = config;

    /// <summary>放弃未保存的编辑，退回基线；只改内存字段，由调用方 RaiseAll 刷新绑定。</summary>
    public void RevertToSnapshot() => ApplyConfig(_snapshot);

    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(_defaultUrl))
        {
            ValidationError = "默认 URL 不能为空";
            return false;
        }

        if (!IsValidRegex(_urlExtractRegex))
        {
            ValidationError = "URL 提取正则无效";
            return false;
        }

        if (!IsValidRegex(_successMarkerRegex))
        {
            ValidationError = "成功标志正则无效";
            return false;
        }

        var colorError = FirstInvalidColor();
        if (colorError is not null)
        {
            ValidationError = colorError;
            return false;
        }

        ValidationError = null;
        return true;
    }

    /// <summary>颜色字段校验：空 = 跟随主题（合法），非空必须是十六进制。不校验的话界面会静默回落主题色，用户只看到「设置疑似不生效」。</summary>
    private string? FirstInvalidColor()
    {
        foreach (var (label, value) in new[]
                 {
                     ("三键图标颜色", _chromeButtonIconColor),
                     ("三键背景颜色", _chromeButtonBackground),
                     ("拖动热区颜色", _dragStripColor),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (!HexColor.TryParse(value, out _, out _, out _, out _))
                return $"{label}不是合法的十六进制颜色：{value.Trim()}（可填 #RGB / #RRGGBB / #AARRGGBB，或留空表示跟随主题）";
        }

        return null;
    }

    public AppConfig BuildConfig()
    {
        return _snapshot with
        {
            DefaultUrl = StripToken(_defaultUrl),
            ServiceStrategy = _serviceStrategy,
            LaunchCommand = _launchCommand,
            UrlExtractRegex = _urlExtractRegex,
            SuccessMarkerRegex = _successMarkerRegex,
            WorkingDirectory = _workingDirectory,
            ChromeButtonsDefault = _chromeButtonsDefault,
            // 落盘前 Trim：带空白的十六进制是「设置不生效」的经典来源
            ChromeButtonIconColor = _chromeButtonIconColor.Trim(),
            ChromeButtonBackground = _chromeButtonBackground.Trim(),
            ChromeHoverDelayMs = _chromeHoverDelayMs,
            DragStripEnabled = _dragStripEnabled,
            DragStripLeftInset = _dragStripLeftInset,
            DragStripRightInset = _dragStripRightInset,
            DragStripHeight = _dragStripHeight,
            DragStripColor = _dragStripColor,
            DragStripOpacity = _dragStripOpacity,
        };
    }

    /// <summary>「重置外观」：把外观字段恢复为出厂默认值，取值须与 <see cref="AppConfig.CreateDefault"/> 逐项一致（由 ResetAppearanceTests 逐字段比对防复发）。</summary>
    public void ResetAppearance()
    {
        ChromeButtonsDefault = ChromeButtonVisibility.Shown;
        ChromeButtonIconColor = string.Empty;
        ChromeButtonBackground = string.Empty;
        ChromeHoverDelayMs = 800;
        DragStripEnabled = true;
        DragStripLeftInset = 280;
        DragStripRightInset = 138;
        DragStripHeight = 40;
        DragStripColor = "#0000FF";
        DragStripOpacity = 0.0;
    }

    public static string StripToken(string url)
    {
        var match = Regex.Match(url, @"^(https?://[^/]+)");
        if (!match.Success)
            return url;
        var baseUri = match.Value;
        return baseUri.EndsWith('/') ? baseUri : baseUri + "/";
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(1));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
