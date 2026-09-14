using System.Windows;
using DshDesktop.Application;
using Microsoft.Win32;

namespace DshDesktop.App;

/// <summary>
/// 主题装配：读系统外观设置 → 替换 App.xaml 里合并的主题资源字典。
/// 只替换字典、不逐个改控件颜色，是因为三键的颜色通路本来就是动态的：
/// 图标色走 <c>SetResourceReference</c>，按钮底色留空时走 <c>DynamicResource</c>，
/// 两者都会跟着字典替换自动变色（见 ChromeBar.Configure）。
/// </summary>
internal static class ThemeSwitcher
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    /// <summary>用来在 MergedDictionaries 里认出主题字典的标记。</summary>
    private const string ThemeDictionaryMarker = "/Themes/";

    /// <summary>
    /// 读系统设置并套用主题。返回注册表原始值，供调用方打日志（读不到时 <c>null</c>）。
    /// </summary>
    public static (AppTheme Theme, int? AppsUseLightTheme) ApplyFromSystem()
    {
        var raw = ReadAppsUseLightTheme();
        var theme = ThemeSelection.FromAppsUseLightTheme(raw);
        Apply(theme);
        return (theme, raw);
    }

    /// <summary>注册表值缺失或读取失败时返回 <c>null</c>（由上层落成深色）。</summary>
    private static int? ReadAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(AppsUseLightThemeValue) as int?;
        }
        catch
        {
            // 权限被拒 / 键不存在都不该影响启动：静默回落默认主题。
            // 原始值会以 (missing) 形式出现在 run.log 的 [theme] 行里，不算静默失败。
            return null;
        }
    }

    private static void Apply(AppTheme theme)
    {
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/Themes/{(theme == AppTheme.Light ? "Light" : "Dark")}.xaml",
                UriKind.Absolute),
        };

        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (source is not null &&
                source.Contains(ThemeDictionaryMarker, StringComparison.OrdinalIgnoreCase))
            {
                dictionaries[i] = replacement;
                return;
            }
        }

        // App.xaml 里没合并任何主题字典时兜底加上，避免主题整体缺失。
        dictionaries.Add(replacement);
    }
}
