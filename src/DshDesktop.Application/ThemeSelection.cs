namespace DshDesktop.Application;

/// <summary>程序主题。只有两档，分别对应 Shell 层的 Themes/Light.xaml 与 Themes/Dark.xaml。</summary>
public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>系统外观设置 → 主题的映射（设计文档 §7.2）。读注册表属 Shell 层职责，这里只留纯映射。</summary>
public static class ThemeSelection
{
    /// <summary>Windows 的 <c>AppsUseLightTheme</c> 值 → 主题。1 = 浅色，其余一律深色（Dark.xaml 是默认字典）。</summary>
    public static AppTheme FromAppsUseLightTheme(int? appsUseLightTheme) =>
        appsUseLightTheme == 1 ? AppTheme.Light : AppTheme.Dark;
}
