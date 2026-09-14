namespace DshDesktop.Application;

/// <summary>程序主题。只有两档，分别对应 Shell 层的 Themes/Light.xaml 与 Themes/Dark.xaml。</summary>
public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>
/// 系统外观设置 → 主题的映射（设计文档 §7.2「颜色配置留空 = 跟随主题（暗色/亮色自动切换）」）。
/// 读注册表属于 Shell 层职责，这里只留可直接单测的纯映射。
/// </summary>
public static class ThemeSelection
{
    /// <summary>
    /// Windows 的 <c>AppsUseLightTheme</c> 值 → 主题。
    /// 1 = 浅色；0 = 深色；<c>null</c>（键/值缺失、读取被拒）= 深色。
    /// 非 0/1 的意外取值同样按深色处理：Dark.xaml 是 App.xaml 里的默认字典，
    /// 「读不到就回落到默认」比「猜一个」更可预测。
    /// </summary>
    public static AppTheme FromAppsUseLightTheme(int? appsUseLightTheme) =>
        appsUseLightTheme == 1 ? AppTheme.Light : AppTheme.Dark;
}
