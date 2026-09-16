namespace DshDesktop.App;

/// <summary>
/// 程序集版本号（来自 csproj 的 <c>&lt;Version&gt;</c>）。放在这里供主窗口与设置窗口共用，
/// 避免两处各写一份解析逻辑后走样。
/// </summary>
internal static class AppVersion
{
    /// <summary>去掉末尾修订号：显示 1.0.1 而非 1.0.1.0。</summary>
    internal static string Short =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "1.0.1";
}
