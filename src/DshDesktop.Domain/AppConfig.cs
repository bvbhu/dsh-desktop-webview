namespace DshDesktop.Domain;

public sealed record AppConfig(
    string DefaultUrl,
    ServiceStrategy ServiceStrategy,
    string LaunchCommand,
    string UrlExtractRegex,
    string SuccessMarkerRegex,
    string WorkingDirectory,
    ChromeButtonVisibility ChromeButtonsDefault,
    string ChromeButtonIconColor,
    string ChromeButtonBackground,
    int ChromeHoverDelayMs,
    bool DragStripEnabled,
    int DragStripLeftInset,
    int DragStripRightInset,
    int DragStripHeight,
    string DragStripColor,
    double DragStripOpacity,
    int WindowX,
    int WindowY,
    int WindowWidth,
    int WindowHeight,
    bool WindowMaximized)
{
    public static AppConfig CreateDefault() => new(
        DefaultUrl: "http://127.0.0.1:3080/",
        ServiceStrategy: ServiceStrategy.ProbeThenStart,
        LaunchCommand: "dsh web --no-open",
        UrlExtractRegex: @"dsh web:\s*(?<url>https?://\S+)",
        // 空串 = 第 2 级（成功标志）禁用，默认不做回退。
        // 空串在 Regex 里会匹配任意行，所以"禁用"必须由 UrlExtractor 显式当成 null 处理，
        // 不能直接把 "" 丢给 new Regex —— 那会让第一行输出就被当成成功标志。
        SuccessMarkerRegex: string.Empty,
        WorkingDirectory: string.Empty,
        ChromeButtonsDefault: ChromeButtonVisibility.Shown,
        ChromeButtonIconColor: string.Empty,
        ChromeButtonBackground: string.Empty,
        ChromeHoverDelayMs: 800,
        DragStripEnabled: true,
        // 热区默认值刻意不同于设计文档 §5.3 的初版取值：280 对齐 DSH 左侧栏宽度、
        // 138 = 三键总宽、#0000FF、透明度 0（视觉上完全不可见，但仍参与命中测试、仍可拖动
        // —— 需要确认位置时到设置窗口临时调高）。
        DragStripLeftInset: 280,
        DragStripRightInset: 138,
        DragStripHeight: 40,
        DragStripColor: "#0000FF",
        DragStripOpacity: 0.0,
        // WindowX/WindowY = 0 是"尚未记忆"的哨兵：居中由 MainWindow 按工作区实时算，
        // 绝不在这里写死坐标（写死了换分辨率/任务栏在侧边的机器就会偏出屏幕）。
        WindowX: 0,
        WindowY: 0,
        WindowWidth: 1500,
        WindowHeight: 750,
        WindowMaximized: false);
}
