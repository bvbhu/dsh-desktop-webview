using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using DshDesktop.Domain;

namespace DshDesktop.Application;

public sealed class SettingsViewModel : ViewModelBase
{
    /// <summary>
    /// 取消勾选「跟随主题」时给一个立即可见的白底 —— 详见
    /// <see cref="ChromeButtonBackgroundFollowTheme"/> 的说明。
    /// </summary>
    private const string DefaultChromeButtonBackground = "#FFFFFF";

    /// <summary>
    /// 上次保存（或启动时加载）的配置，作为「取消」的回退基线。
    /// 不是 readonly：保存成功时必须推进基线，否则「取消」会回退到启动时的旧值。
    /// </summary>
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
            // 派生视图跟着刷：选了「不启动」就要实时藏起启动配置（见 ServiceNeverStart）
            Raise(nameof(ServiceNeverStart));
        }
    }

    /// <summary>
    /// 是否选择了「不启动，直接打开url」。为 true 时启动策略卡片里的
    /// 工作目录 / 启动命令 / 两个正则整块实时隐藏 —— 不启动服务就没有可配置的启动参数，
    /// 留着只会让人误以为它们还有效。
    /// </summary>
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
            // 「跟随主题」复选框是这个字段的派生视图，字段一动它就得跟着刷新
            Raise(nameof(ChromeButtonBackgroundFollowTheme));
        }
    }

    /// <summary>
    /// 「窗口控制按钮 底色」是否跟随主题 —— 即 <see cref="ChromeButtonBackground"/> 是否为空。
    /// <para>
    /// 为什么做成派生属性而不是再存一个 bool 字段：底色的"留空 = 跟随主题"这条语义
    /// 由 <see cref="AppConfig.ChromeButtonBackground"/> 单独承载（<c>ChromeBar.Configure</c> 就是
    /// 按空/非空二分来决定 ClearValue 还是设本地值的）。多存一个 bool 就多一处可能不同步的状态，
    /// 迟早会出现"勾了选框但底色还在"这类幽灵配置。
    /// </para>
    /// <para>
    /// 取消勾选时给 <c>#FFFFFF</c> 而不是留空：留空等于又回到跟随主题，复选框会立刻弹回去，
    /// 用户会觉得点不动。给一个具体的白底，用户再点「选择颜色」去改。
    /// </para>
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
    // 颜色 / 不透明度这两个属性一动，合成值 DragStripArgb 也跟着变（颜色选择器绑的是它）
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
    /// 拖动层颜色 + 不透明度合成的 ARGB 值，给"能选透明度的颜色选择器"用。
    /// <para>
    /// 用户要求：颜色和透明度共用一个选择器、不给输入框（参考图 4 的红字批注）。
    /// 但 <see cref="AppConfig"/> 的 <c>DragStripColor</c> / <c>DragStripOpacity</c> 两个字段
    /// 保持不变（跨层契约不动），拆/合只发生在这对访问器里。
    /// </para>
    /// <para>
    /// 非法值一律<b>原样透出</b>、不做静默修正：校验器要能看到它并报错，
    /// 否则又是"设置疑似不生效"那一类问题（见 <c>FirstInvalidColor</c>）。
    /// </para>
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

            // Set 的名字必须显式给：这里 CallerMemberName 拿到的是 DragStripArgb，
            // 那样会发出错误的属性通知（颜色字段的绑定收不到）。
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

    // MemberNotNull：赋值拆到方法里后编译器看不到字段已初始化，需要显式声明（Nullable 严格模式下必须）。
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

    /// <summary>保存成功后把当前配置立为新基线（此后「取消」回退到这里）。</summary>
    public void AcceptSnapshot(AppConfig config) => _snapshot = config;

    /// <summary>
    /// 放弃未保存的编辑，退回基线。
    /// 只改内存字段，不发通知；调用方（设置窗口）负责 RaiseAll 刷新绑定。
    /// </summary>
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

    /// <summary>
    /// 颜色字段校验：空 = 跟随主题（合法）；非空则必须是十六进制。
    /// <para>
    /// 为什么必须校验而不是让界面自己回落：颜色解析失败时界面会静默用主题色，
    /// 用户看到的只是"设置疑似不生效"（2026-09-13 实测踩过：<c>" #123456 "</c> 带空格就被
    /// WPF 的 ColorConverter 抛掉）。这里把话说清楚，用户才知道该怎么改。
    /// </para>
    /// </summary>
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
            // 颜色落盘前 Trim：带空白的十六进制是"设置不生效"的经典来源（见 FirstInvalidColor）
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

    /// <summary>
    /// 「重置外观」：把外观字段恢复为出厂默认值。
    /// <para>
    /// 取值必须与 <see cref="AppConfig.CreateDefault"/> 逐项一致 —— 外观字段散在公开 record 里，
    /// 没法整体替换，只能逐项赋值，这份清单是它的手工副本。漏同步会让重置结果与新装默认不一致，
    /// 由 <c>ResetAppearanceTests</c> 逐字段比对防复发。
    /// </para>
    /// </summary>
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
